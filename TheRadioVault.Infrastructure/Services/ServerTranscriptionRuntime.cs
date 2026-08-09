using TheRadioVault.Data.Database;
using TheRadioVault.Desktop.Avalonia.Diarization;
using TheRadioVault.Desktop.Avalonia.Transcription;
using TheRadioVault.Services.Jobs;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;
using TheRadioVault.Transcription.Services;

namespace TheRadioVault.Services;

/// <summary>
/// Long-lived transcription owner for the dedicated server. Repository startup
/// recovery runs once, and workers remain alive when every client is closed.
/// </summary>
public sealed class ServerTranscriptionRuntime : IDisposable
{
    private bool _disposed;

    public ServerTranscriptionRuntime(SqliteDatabase database, IBackgroundJobQueue jobs, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(jobs);
        if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("A server data directory is required.", nameof(dataDirectory));

        SettingsStore = new WhisperCppSettingsStore(Path.Combine(dataDirectory, "transcription.json"));
        Downloads = new WhisperDownloadService(Path.Combine(dataDirectory, "Transcription"));
        TranscriptRepository = new SqliteTranscriptRepository(database);
        SpeakerRepository = new SqliteSpeakerIdentityRepository(database);
        BatchRepository = new SqliteTranscriptionBatchRepository(database);
        Engine = new WhisperCppTranscriptionEngine(
            SettingsStore.Load(),
            OperatingSystem.IsWindows() ? new WindowsTranscriptionProcessController() : null);
        DiarizationEngine = new SherpaOnnxMultiSpeakerDiarizationEngine(Engine);
        VoiceEmbeddingEngine = new SherpaOnnxVoiceEmbeddingEngine(Engine);
        AudioPreparer = new NAudioTranscriptionAudioPreparer();
        Coordinator = new TranscriptionCoordinator(
            TranscriptRepository,
            Engine,
            jobs,
            DiarizationEngine,
            AudioPreparer);
        BatchCoordinator = new TranscriptionBatchCoordinator(BatchRepository, TranscriptRepository, Coordinator);
        VoiceLearning = new VoiceLearningCoordinator(SpeakerRepository, TranscriptRepository, VoiceEmbeddingEngine);
    }

    public WhisperCppSettingsStore SettingsStore { get; }
    public WhisperDownloadService Downloads { get; }
    public ITranscriptRepository TranscriptRepository { get; }
    public ISpeakerIdentityRepository SpeakerRepository { get; }
    public ITranscriptionBatchRepository BatchRepository { get; }
    public WhisperCppTranscriptionEngine Engine { get; }
    public IMultiSpeakerDiarizationEngine DiarizationEngine { get; }
    public IVoiceEmbeddingEngine VoiceEmbeddingEngine { get; }
    public ITranscriptionAudioPreparer AudioPreparer { get; }
    public ITranscriptionCoordinator Coordinator { get; }
    public ITranscriptionBatchCoordinator BatchCoordinator { get; }
    public IVoiceLearningCoordinator VoiceLearning { get; }

    public WhisperCppEngineSettings GetSettings() => Engine.GetSettings();

    public WhisperCppEngineSettings SaveSettings(WhisperCppEngineSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SettingsStore.Save(settings);
        var saved = SettingsStore.Load();
        Engine.Configure(saved);
        return saved;
    }

    public WhisperCppEngineSettings ReloadSettings()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var settings = SettingsStore.Load();
        Engine.Configure(settings);
        return settings;
    }

    public ServerTranscriptionStatus GetStatus()
        => new(
            Engine.IsAvailable,
            Engine.AvailabilityMessage,
            Engine.Id,
            Engine.Version,
            Engine.GetSettings().ModelId,
            DiarizationEngine.IsAvailable,
            DiarizationEngine.AvailabilityMessage,
            VoiceEmbeddingEngine.IsAvailable);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        BatchCoordinator.Dispose();
        Downloads.Dispose();
    }
}

public sealed record ServerTranscriptionStatus(
    bool IsAvailable,
    string AvailabilityMessage,
    string EngineId,
    string EngineVersion,
    string ModelId,
    bool DiarizationAvailable,
    string DiarizationMessage,
    bool VoiceLearningAvailable);
