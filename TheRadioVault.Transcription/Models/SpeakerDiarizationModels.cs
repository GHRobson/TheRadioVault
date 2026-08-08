namespace TheRadioVault.Transcription.Models;

public sealed record SpeakerDiarizationRequest(
    string AudioPath,
    long StartMs = 0,
    long? DurationMs = null,
    double ClusteringThreshold = 0.9,
    int ExpectedSpeakerCount = 0);

public sealed record SpeakerDiarizationTurn(
    long StartMs,
    long EndMs,
    string SpeakerKey,
    string SpeakerLabel);

public sealed record SpeakerDiarizationResult(
    IReadOnlyList<SpeakerDiarizationTurn> Turns,
    int SpeakerCount,
    string EngineId,
    string EngineVersion,
    string SegmentationModel,
    string EmbeddingModel);
