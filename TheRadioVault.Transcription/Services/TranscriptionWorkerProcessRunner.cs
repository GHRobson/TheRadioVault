using System.Diagnostics;
using System.Threading.Channels;
using TheRadioVault.Transcription.Contracts;

namespace TheRadioVault.Transcription.Services;

public sealed record WhisperWorkerPolicy
{
    public static WhisperWorkerPolicy Default { get; } = new(TimeSpan.FromMinutes(10));

    public WhisperWorkerPolicy(TimeSpan inactivityTimeout)
    {
        if (inactivityTimeout <= TimeSpan.Zero || inactivityTimeout.TotalMilliseconds > uint.MaxValue - 1)
            throw new ArgumentOutOfRangeException(nameof(inactivityTimeout), "The worker inactivity timeout must be positive and no longer than 49 days.");
        InactivityTimeout = inactivityTimeout;
    }

    public TimeSpan InactivityTimeout { get; }
}

public sealed class WhisperWorkerTimeoutException : TimeoutException
{
    public WhisperWorkerTimeoutException(TimeSpan inactivityTimeout)
        : base($"The whisper.cpp worker stopped reporting activity for {FormatDuration(inactivityTimeout)}. Its process was stopped; retry the transcription.")
    {
        InactivityTimeout = inactivityTimeout;
    }

    public TimeSpan InactivityTimeout { get; }

    private static string FormatDuration(TimeSpan value)
        => value.TotalMinutes >= 1
            ? $"{value.TotalMinutes:0.#} minutes"
            : value.TotalSeconds >= 1
                ? $"{value.TotalSeconds:0.#} seconds"
                : $"{value.TotalMilliseconds:0} milliseconds";
}

public sealed class TranscriptionWorkerProcessRunner : ITranscriptionWorkerProcessRunner
{
    private static readonly TimeSpan TerminationGracePeriod = TimeSpan.FromSeconds(5);
    private readonly ITranscriptionWorkerProcessFactory _processFactory;

    public TranscriptionWorkerProcessRunner()
        : this(new SystemTranscriptionWorkerProcessFactory())
    {
    }

    internal TranscriptionWorkerProcessRunner(ITranscriptionWorkerProcessFactory processFactory)
    {
        _processFactory = processFactory ?? throw new ArgumentNullException(nameof(processFactory));
    }

    public async Task<int> RunAsync(
        TranscriptionWorkerProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.StartInfo);
        _ = new WhisperWorkerPolicy(request.InactivityTimeout);
        cancellationToken.ThrowIfCancellationRequested();

        using var process = _processFactory.Create(request.StartInfo);
        var activity = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });

        process.StandardOutputReceived += line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            activity.Writer.TryWrite(true);
            request.StandardOutputReceived?.Invoke(line);
        };
        process.StandardErrorReceived += line =>
        {
            if (string.IsNullOrWhiteSpace(line)) return;
            activity.Writer.TryWrite(true);
            request.StandardErrorReceived?.Invoke(line);
        };

        if (!process.Start()) throw new InvalidOperationException("whisper.cpp could not be started.");
        var exitTask = process.WaitForExitAsync();

        try
        {
            request.ProcessStarted?.Invoke(process.Id);
            process.BeginOutputRead();
            while (!exitTask.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                deadline.CancelAfter(request.InactivityTimeout);
                var activityTask = activity.Reader.ReadAsync(deadline.Token).AsTask();
                var completed = await Task.WhenAny(exitTask, activityTask).ConfigureAwait(false);
                if (completed == exitTask) break;

                try
                {
                    await activityTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    throw new WhisperWorkerTimeoutException(request.InactivityTimeout);
                }

                while (activity.Reader.TryRead(out _))
                {
                }
            }

            await exitTask.ConfigureAwait(false);
            return process.ExitCode;
        }
        catch
        {
            await TerminateAsync(process, exitTask).ConfigureAwait(false);
            throw;
        }
        finally
        {
            activity.Writer.TryComplete();
        }
    }

    private static async Task TerminateAsync(
        ITranscriptionWorkerProcess process,
        Task exitTask)
    {
        process.TryKillTree();
        if (exitTask.IsCompleted) return;
        await Task.WhenAny(exitTask, Task.Delay(TerminationGracePeriod)).ConfigureAwait(false);
    }
}

internal interface ITranscriptionWorkerProcessFactory
{
    ITranscriptionWorkerProcess Create(ProcessStartInfo startInfo);
}

internal interface ITranscriptionWorkerProcess : IDisposable
{
    event Action<string>? StandardOutputReceived;
    event Action<string>? StandardErrorReceived;
    int Id { get; }
    int ExitCode { get; }
    bool Start();
    void BeginOutputRead();
    Task WaitForExitAsync();
    void TryKillTree();
}

internal sealed class SystemTranscriptionWorkerProcessFactory : ITranscriptionWorkerProcessFactory
{
    public ITranscriptionWorkerProcess Create(ProcessStartInfo startInfo)
        => new SystemTranscriptionWorkerProcess(startInfo);
}

internal sealed class SystemTranscriptionWorkerProcess : ITranscriptionWorkerProcess
{
    private readonly Process _process;

    public SystemTranscriptionWorkerProcess(ProcessStartInfo startInfo)
    {
        _process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true
        };
        _process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null) StandardOutputReceived?.Invoke(eventArgs.Data);
        };
        _process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null) StandardErrorReceived?.Invoke(eventArgs.Data);
        };
    }

    public event Action<string>? StandardOutputReceived;
    public event Action<string>? StandardErrorReceived;
    public int Id => _process.Id;
    public int ExitCode => _process.ExitCode;
    public bool Start() => _process.Start();

    public void BeginOutputRead()
    {
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public Task WaitForExitAsync() => _process.WaitForExitAsync();

    public void TryKillTree()
    {
        try
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Exit and kill can race, and a platform may already have reaped the worker.
        }
    }

    public void Dispose() => _process.Dispose();
}
