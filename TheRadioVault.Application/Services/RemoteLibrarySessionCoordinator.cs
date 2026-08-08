using System.Diagnostics;
using TheRadioVault.Application.Models;

namespace TheRadioVault.Application.Services;

/// <summary>
/// Platform-neutral owner of a remote Library session's synchronization cursor,
/// single-flight gate, timeout/cancellation lifetime, reconnect backoff and
/// diagnostics. Presentation code remains responsible only for applying the
/// returned Library data to its controls.
/// </summary>
public sealed class RemoteLibrarySessionCoordinator : IDisposable
{
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _stateGate = new();
    private CancellationTokenSource? _activeSync;
    private RemoteLibrarySessionState _state = RemoteLibrarySessionState.Disconnected;
    private RemoteLibrarySyncCursor _cursor = RemoteLibrarySyncCursor.Empty;
    private DateTimeOffset? _lastLiveAt;
    private int _consecutiveFailures;
    private DateTimeOffset? _nextReconnectAt;
    private bool _syncInProgress;
    private bool _hasUsableSnapshot;
    private bool _isCacheOnly;
    private bool _disposed;
    private RemoteLibrarySyncDiagnostics _diagnostics = RemoteLibrarySyncDiagnostics.Empty;

    public RemoteLibrarySessionSnapshot Current
    {
        get
        {
            lock (_stateGate)
            {
                return SnapshotUnsafe();
            }
        }
    }

    public bool CanSynchronize(DateTimeOffset now)
    {
        lock (_stateGate)
        {
            return !_disposed &&
                   _state != RemoteLibrarySessionState.Closing &&
                   !_syncInProgress &&
                   (!_nextReconnectAt.HasValue || now >= _nextReconnectAt.Value);
        }
    }

    public void AdoptCachedSnapshot(RemoteLibrarySyncCursor cursor, DateTimeOffset lastLiveAt)
    {
        lock (_stateGate)
        {
            ThrowIfDisposedUnsafe();
            _cursor = cursor.Normalize();
            _lastLiveAt = lastLiveAt == default ? null : lastLiveAt;
            _state = RemoteLibrarySessionState.CachedReadOnly;
            _hasUsableSnapshot = true;
            _isCacheOnly = true;
            _nextReconnectAt = null;
        }
    }

    public void RequestReconnectNow()
    {
        lock (_stateGate)
        {
            if (_disposed || _state == RemoteLibrarySessionState.Closing) return;
            _nextReconnectAt = null;
        }
    }

    public void BeginClosing()
    {
        CancellationTokenSource? active;
        lock (_stateGate)
        {
            if (_disposed) return;
            _state = RemoteLibrarySessionState.Closing;
            _nextReconnectAt = null;
            active = _activeSync;
        }
        try { active?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public async Task<RemoteLibrarySyncLease?> BeginSyncAsync(
        RemoteLibrarySyncRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(request), "Remote synchronization timeout must be positive.");

        lock (_stateGate)
        {
            ThrowIfDisposedUnsafe();
            if (_state == RemoteLibrarySessionState.Closing || _syncInProgress) return null;
            _syncInProgress = true;
        }

        var gateHeld = false;
        try
        {
            await _syncGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateHeld = true;

            CancellationTokenSource linked;
            RemoteLibrarySyncCursor cursor;
            lock (_stateGate)
            {
                ThrowIfDisposedUnsafe();
                if (_state == RemoteLibrarySessionState.Closing)
                {
                    _syncInProgress = false;
                    _syncGate.Release();
                    return null;
                }

                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linked.CancelAfter(request.Timeout);
                _activeSync = linked;
                _state = request.InitialLoad
                    ? RemoteLibrarySessionState.Connecting
                    : RemoteLibrarySessionState.Updating;
                cursor = request.ForceReset ? RemoteLibrarySyncCursor.Empty : _cursor;
            }

            return new RemoteLibrarySyncLease(this, request, cursor, linked, Stopwatch.StartNew());
        }
        catch
        {
            lock (_stateGate) _syncInProgress = false;
            if (gateHeld) _syncGate.Release();
            throw;
        }
    }

    internal RemoteLibrarySessionSnapshot CompleteSuccess(
        RemoteLibrarySyncCursor cursor,
        RemoteLibrarySyncMetrics metrics)
    {
        var normalizedMetrics = metrics.Normalize();
        lock (_stateGate)
        {
            ThrowIfDisposedUnsafe();
            _cursor = cursor.Normalize();
            _lastLiveAt = DateTimeOffset.UtcNow;
            _consecutiveFailures = 0;
            _nextReconnectAt = null;
            _state = RemoteLibrarySessionState.Live;
            _hasUsableSnapshot = true;
            _isCacheOnly = false;
            _diagnostics = new RemoteLibrarySyncDiagnostics(
                DateTimeOffset.UtcNow,
                normalizedMetrics.Mode,
                normalizedMetrics.ElapsedMilliseconds,
                normalizedMetrics.ChangedRows,
                string.Empty);
            return SnapshotUnsafe();
        }
    }

    internal RemoteLibraryFailurePlan CompleteFailure(
        string message,
        bool hasUsableSnapshot,
        long elapsedMilliseconds)
    {
        lock (_stateGate)
        {
            ThrowIfDisposedUnsafe();
            _consecutiveFailures++;
            var retrySeconds = Math.Min(
                60,
                3 * (int)Math.Pow(2, Math.Min(4, _consecutiveFailures - 1)));
            var retryDelay = TimeSpan.FromSeconds(retrySeconds);
            _nextReconnectAt = DateTimeOffset.UtcNow.Add(retryDelay);
            _hasUsableSnapshot = hasUsableSnapshot;
            _isCacheOnly = hasUsableSnapshot;
            _state = hasUsableSnapshot
                ? RemoteLibrarySessionState.CachedReadOnly
                : RemoteLibrarySessionState.Unavailable;
            var trimmed = (message ?? string.Empty).Trim();
            if (trimmed.Length > 500) trimmed = trimmed[..500];
            _diagnostics = new RemoteLibrarySyncDiagnostics(
                DateTimeOffset.UtcNow,
                "failed",
                Math.Max(0, elapsedMilliseconds),
                0,
                trimmed);
            return new RemoteLibraryFailurePlan(SnapshotUnsafe(), retryDelay, hasUsableSnapshot);
        }
    }

    internal void ReleaseSync(CancellationTokenSource cancellation)
    {
        lock (_stateGate)
        {
            if (ReferenceEquals(_activeSync, cancellation)) _activeSync = null;
            _syncInProgress = false;
        }
        cancellation.Dispose();
        _syncGate.Release();
    }

    private RemoteLibrarySessionSnapshot SnapshotUnsafe()
        => new(
            _state,
            _cursor,
            _lastLiveAt,
            _consecutiveFailures,
            _nextReconnectAt,
            _syncInProgress,
            _hasUsableSnapshot,
            _isCacheOnly,
            _diagnostics);

    private void ThrowIfDisposedUnsafe()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RemoteLibrarySessionCoordinator));
    }

    public void Dispose()
    {
        CancellationTokenSource? active;
        lock (_stateGate)
        {
            if (_disposed) return;
            _disposed = true;
            _state = RemoteLibrarySessionState.Closing;
            active = _activeSync;
            _activeSync = null;
        }
        try { active?.Cancel(); }
        catch (ObjectDisposedException) { }
        active?.Dispose();
        _syncGate.Dispose();
    }
}

public sealed class RemoteLibrarySyncLease : IDisposable
{
    private readonly RemoteLibrarySessionCoordinator _owner;
    private readonly CancellationTokenSource _cancellation;
    private readonly Stopwatch _stopwatch;
    private int _completed;
    private int _disposed;

    internal RemoteLibrarySyncLease(
        RemoteLibrarySessionCoordinator owner,
        RemoteLibrarySyncRequest request,
        RemoteLibrarySyncCursor cursor,
        CancellationTokenSource cancellation,
        Stopwatch stopwatch)
    {
        _owner = owner;
        Request = request;
        Cursor = cursor;
        _cancellation = cancellation;
        _stopwatch = stopwatch;
    }

    public RemoteLibrarySyncRequest Request { get; }
    public RemoteLibrarySyncCursor Cursor { get; }
    public CancellationToken Token => _cancellation.Token;
    public long ElapsedMilliseconds => Math.Max(0, _stopwatch.ElapsedMilliseconds);
    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public RemoteLibrarySessionSnapshot CompleteSuccess(
        RemoteLibrarySyncCursor cursor,
        RemoteLibrarySyncMetrics metrics)
    {
        EnsureCanComplete();
        Interlocked.Exchange(ref _completed, 1);
        _stopwatch.Stop();
        return _owner.CompleteSuccess(
            cursor,
            metrics with { ElapsedMilliseconds = ElapsedMilliseconds });
    }

    public RemoteLibraryFailurePlan CompleteFailure(string message, bool hasUsableSnapshot)
    {
        EnsureCanComplete();
        Interlocked.Exchange(ref _completed, 1);
        _stopwatch.Stop();
        return _owner.CompleteFailure(message, hasUsableSnapshot, ElapsedMilliseconds);
    }

    private void EnsureCanComplete()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw new ObjectDisposedException(nameof(RemoteLibrarySyncLease));
        if (Volatile.Read(ref _completed) != 0)
            throw new InvalidOperationException("The remote synchronization lease has already been completed.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopwatch.Stop();
        _owner.ReleaseSync(_cancellation);
    }
}
