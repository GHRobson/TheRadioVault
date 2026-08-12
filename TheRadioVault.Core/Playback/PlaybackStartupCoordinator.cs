namespace TheRadioVault.Core.Playback;

/// <summary>
/// Serialises decoder startup and gives the newest explicit playback request
/// ownership of the startup pipeline. Superseded requests are cancelled before
/// the next request is allowed to touch the shared platform decoder.
/// </summary>
public sealed class PlaybackStartupCoordinator : IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _startupGate = new(1, 1);
    private CancellationTokenSource? _activeCancellation;
    private long _generation;
    private bool _disposed;

    public PlaybackStartupAttempt Begin(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeCancellation?.Cancel();
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _activeCancellation = linked;
            return new PlaybackStartupAttempt(
                this,
                Interlocked.Increment(ref _generation),
                linked,
                cancellationToken);
        }
    }

    internal async ValueTask EnterAsync(PlaybackStartupAttempt attempt)
    {
        await _startupGate.WaitAsync(attempt.CancellationToken).ConfigureAwait(false);
        attempt.MarkEntered();
        attempt.ThrowIfCancelledOrSuperseded();
    }

    internal bool IsCurrent(PlaybackStartupAttempt attempt)
    {
        lock (_gate)
            return !_disposed &&
                   ReferenceEquals(_activeCancellation, attempt.Cancellation) &&
                   attempt.Generation == Volatile.Read(ref _generation);
    }

    internal void Complete(PlaybackStartupAttempt attempt, bool entered)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_activeCancellation, attempt.Cancellation))
                _activeCancellation = null;
        }

        if (entered) _startupGate.Release();
        attempt.Cancellation.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _activeCancellation?.Cancel();
            _activeCancellation = null;
        }
    }
}

public sealed class PlaybackStartupAttempt : IAsyncDisposable
{
    private readonly PlaybackStartupCoordinator _owner;
    private readonly CancellationToken _callerCancellationToken;
    private bool _entered;
    private bool _disposed;

    internal PlaybackStartupAttempt(
        PlaybackStartupCoordinator owner,
        long generation,
        CancellationTokenSource cancellation,
        CancellationToken callerCancellationToken)
    {
        _owner = owner;
        Generation = generation;
        Cancellation = cancellation;
        _callerCancellationToken = callerCancellationToken;
    }

    public long Generation { get; }
    public CancellationToken CancellationToken => Cancellation.Token;
    public bool IsCurrent => !_disposed && _owner.IsCurrent(this);
    public bool IsSuperseded => !_disposed && !IsCurrent && !_callerCancellationToken.IsCancellationRequested;
    internal CancellationTokenSource Cancellation { get; }

    public ValueTask EnterAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _owner.EnterAsync(this);
    }

    public async Task WaitForReadinessAsync(
        Func<bool> isReady,
        Func<string?> failureMessage,
        TimeSpan timeout,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(isReady);
        ArgumentNullException.ThrowIfNull(failureMessage);
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var interval = pollInterval ?? TimeSpan.FromMilliseconds(50);
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(pollInterval));

        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (true)
        {
            ThrowIfCancelledOrSuperseded();
            var failure = failureMessage();
            if (!string.IsNullOrWhiteSpace(failure))
                throw new PlaybackStartupUnavailableException(failure);
            if (isReady()) return;
            if (DateTimeOffset.UtcNow >= deadline)
                throw new PlaybackStartupTimeoutException(timeout);

            var remaining = deadline - DateTimeOffset.UtcNow;
            await Task.Delay(remaining < interval ? remaining : interval, CancellationToken)
                .ConfigureAwait(false);
        }
    }

    public void ThrowIfCancelledOrSuperseded()
    {
        if (!CancellationToken.IsCancellationRequested)
        {
            if (IsCurrent) return;
            throw new PlaybackStartupSupersededException();
        }

        if (IsSuperseded)
            throw new PlaybackStartupSupersededException();
        CancellationToken.ThrowIfCancellationRequested();
    }

    internal void MarkEntered() => _entered = true;

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _owner.Complete(this, _entered);
        return ValueTask.CompletedTask;
    }
}

public sealed class PlaybackStartupSupersededException : OperationCanceledException
{
    public PlaybackStartupSupersededException()
        : base("A newer playback request replaced this decoder startup.") { }
}

public sealed class PlaybackStartupTimeoutException : TimeoutException
{
    public PlaybackStartupTimeoutException(TimeSpan timeout)
        : base($"The playback engine did not become ready within {timeout.TotalSeconds:0.#} seconds.")
    {
        Timeout = timeout;
    }

    public TimeSpan Timeout { get; }
}

public sealed class PlaybackStartupUnavailableException : InvalidOperationException
{
    public PlaybackStartupUnavailableException(string message) : base(message) { }
}
