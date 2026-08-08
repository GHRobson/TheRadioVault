using System.Text.Json.Serialization;

namespace TheRadioVault.Transcription.Models;

public enum SpeakerAssignmentState
{
    Unassigned,
    Suggested,
    Confirmed
}

public enum VoiceSampleState
{
    Pending,
    Ready,
    Rejected,
    Failed
}

public sealed class TranscriptSpeakerCluster
{
    [JsonIgnore]
    public long Id { get; init; }
    [JsonIgnore]
    public long TranscriptId { get; init; }
    public string SpeakerKey { get; init; } = "";
    public string Label { get; init; } = "";
    public int SegmentCount { get; init; }
    public long SpeakingDurationMs { get; init; }
    [JsonIgnore]
    public long? VoicePersonId { get; init; }
    public string PersonName { get; init; } = "";
    public SpeakerAssignmentState AssignmentState { get; init; } = SpeakerAssignmentState.Unassigned;
    public double? AssignmentConfidence { get; init; }
    public string AssignmentSource { get; init; } = "";
    public bool TrainVoice { get; init; } = true;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    public string DisplayName => !string.IsNullOrWhiteSpace(PersonName)
        ? PersonName
        : !string.IsNullOrWhiteSpace(Label) ? Label : SpeakerKey;

    public string AssignmentDisplay => AssignmentState switch
    {
        SpeakerAssignmentState.Confirmed when !string.IsNullOrWhiteSpace(PersonName) => $"Confirmed as {PersonName}",
        SpeakerAssignmentState.Suggested when !string.IsNullOrWhiteSpace(PersonName) => $"Suggested: {PersonName}",
        _ => "Unassigned"
    };

    public string EvidenceDisplay
    {
        get
        {
            var duration = TimeSpan.FromMilliseconds(Math.Max(0, SpeakingDurationMs));
            var durationText = duration.TotalHours >= 1
                ? duration.ToString(@"h\:mm\:ss")
                : duration.ToString(@"m\:ss");
            return $"{SegmentCount:N0} segments · {durationText}";
        }
    }
}

public sealed record TranscriptPersonCandidate(string Name, string Role)
{
    public string Display => string.IsNullOrWhiteSpace(Role) ? Name : $"{Name} · {Role}";
}

public sealed class VoicePersonRecord
{
    public long Id { get; init; }
    public string CanonicalName { get; init; } = "";
    public string NormalizedName { get; init; } = "";
    public string AliasesJson { get; init; } = "[]";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed class VoiceProfileSummary
{
    public long VoicePersonId { get; init; }
    public string PersonName { get; init; } = "";
    public int ConfirmedClusterCount { get; init; }
    public int PendingSampleCount { get; init; }
    public int ReadySampleCount { get; init; }
    public int BroadcastCount { get; init; }
    public string EmbeddingModelId { get; init; } = "";
    public int ProfileRevision { get; init; }
    public DateTimeOffset? LastUpdatedAt { get; init; }

    public string MemoryDisplay => $"{BroadcastCount:N0} broadcasts · {ConfirmedClusterCount:N0} confirmed voices · {ReadySampleCount:N0} learned samples";
}

public sealed class VoiceSampleRecord
{
    public long Id { get; init; }
    public long VoicePersonId { get; init; }
    public string PersonName { get; init; } = "";
    public long EpisodeId { get; init; }
    public long TranscriptId { get; init; }
    public string SpeakerKey { get; init; } = "";
    public long StartMs { get; init; }
    public long EndMs { get; init; }
    public VoiceSampleState State { get; init; } = VoiceSampleState.Pending;
    public string EmbeddingModelId { get; init; } = "";
    public string EmbeddingModelVersion { get; init; } = "";
    public string EmbeddingJson { get; init; } = "";
    public double? QualityScore { get; init; }
    public bool ConfirmedByUser { get; init; }
    public string Error { get; init; } = "";
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ProcessedAt { get; init; }
}

public sealed class SpeakerMatchSuggestion
{
    public long TranscriptSpeakerId { get; init; }
    public long VoicePersonId { get; init; }
    public string PersonName { get; init; } = "";
    public double Confidence { get; init; }
    public double? Distance { get; init; }
    public string EmbeddingModelId { get; init; } = "";
    public int ProfileRevision { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}

public sealed record SpeakerAssignmentResult(
    long EpisodeId,
    long TranscriptId,
    string SpeakerKey,
    string PersonName,
    SpeakerAssignmentState State,
    int PendingSamplesCreated,
    VoiceProfileSummary Profile);

public sealed record VoiceEmbeddingRequest(
    long EpisodeId,
    string AudioPath,
    long StartMs,
    long EndMs,
    string SpeakerKey,
    string WorkingDirectory = "");

public sealed record VoiceEmbeddingResult(
    string ModelId,
    string ModelVersion,
    IReadOnlyList<double> Values,
    double? QualityScore = null);

public sealed record VoiceLearningProgress(
    int Completed,
    int Total,
    string PersonName,
    string Message)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Completed * 100d / Total, 0, 100);
}
