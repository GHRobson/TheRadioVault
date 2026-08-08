using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Contracts;

public sealed record ServerTranscriptionAdministrationStatus(
    bool IsAvailable,
    string AvailabilityMessage,
    string EngineId,
    string EngineVersion,
    string ModelId,
    bool DiarizationAvailable,
    string DiarizationMessage,
    bool VoiceLearningAvailable);

public sealed record ServerTranscriptionAdministrationSnapshot(
    ServerTranscriptionAdministrationStatus Status,
    WhisperCppEngineSettings Settings);

/// <summary>
/// Server-owned transcription setup exposed to every native client. Paths in
/// these settings belong to the active server computer, never the client.
/// </summary>
public interface IServerTranscriptionAdministrationService
{
    Task<ServerTranscriptionAdministrationSnapshot> GetAsync(CancellationToken cancellationToken = default);
    Task<ServerTranscriptionAdministrationSnapshot> SaveAsync(
        WhisperCppEngineSettings settings,
        CancellationToken cancellationToken = default);
    Task<ServerTranscriptionAdministrationSnapshot> InstallRecommendedAsync(
        string modelId,
        CancellationToken cancellationToken = default);
}
