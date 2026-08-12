using System.Diagnostics;

namespace TheRadioVault.Transcription.Contracts;

public sealed record TranscriptionWorkerProcessRequest(
    ProcessStartInfo StartInfo,
    TimeSpan InactivityTimeout,
    Action<int>? ProcessStarted = null,
    Action<string>? StandardOutputReceived = null,
    Action<string>? StandardErrorReceived = null);

public interface ITranscriptionWorkerProcessRunner
{
    Task<int> RunAsync(
        TranscriptionWorkerProcessRequest request,
        CancellationToken cancellationToken);
}
