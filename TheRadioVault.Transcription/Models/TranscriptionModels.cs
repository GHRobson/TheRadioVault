using System.Text.Json.Serialization;

namespace TheRadioVault.Transcription.Models;

public enum TranscriptStatus
{
    Draft,
    Complete,
    Failed
}

public enum TranscriptContentKind
{
    Speech,
    Music,
    Silence,
    NonSpeech,
    Unknown
}

public enum TranscriptionJobState
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled,
    Interrupted
}

public sealed record TranscriptWord(
    long StartMs,
    long EndMs,
    string Text,
    double? Confidence = null,
    string SpeakerKey = "",
    string PersonName = "");

public sealed record TranscriptSegment(
    int Index,
    long StartMs,
    long EndMs,
    string Text,
    string Speaker = "",
    double? Confidence = null,
    IReadOnlyList<TranscriptWord>? Words = null,
    string SpeakerKey = "",
    string AssignedPersonName = "",
    double? SpeakerConfidence = null,
    SpeakerAssignmentState AssignmentState = SpeakerAssignmentState.Unassigned,
    TranscriptContentKind ContentKind = TranscriptContentKind.Speech,
    bool IsReviewed = false)
{
    [JsonIgnore]
    public string TimeDisplay => FormatTime(StartMs);

    [JsonIgnore]
    public bool NeedsReview => !IsReviewed
        && ContentKind == TranscriptContentKind.Speech
        && Confidence is < 0.80;

    [JsonIgnore]
    public string ConfidenceDisplay => Confidence.HasValue ? $"{Confidence.Value:P0}" : "—";

    [JsonIgnore]
    public string QualityDisplay => ContentKind switch
    {
        TranscriptContentKind.Music => "Music",
        TranscriptContentKind.Silence => "Silence",
        TranscriptContentKind.NonSpeech => "Non-speech",
        TranscriptContentKind.Unknown => "Unknown",
        _ when IsReviewed => "Reviewed",
        _ when NeedsReview => "Review",
        _ => "Speech"
    };

    [JsonIgnore]
    public string ReviewStateDisplay => ContentKind == TranscriptContentKind.Speech
        ? IsReviewed ? "Reviewed" : "Needs attention"
        : QualityDisplay;

    [JsonIgnore]
    public string DisplaySpeaker => !string.IsNullOrWhiteSpace(AssignedPersonName)
        ? AssignmentState == SpeakerAssignmentState.Suggested
            ? $"{AssignedPersonName} (suggested)"
            : AssignedPersonName
        : !string.IsNullOrWhiteSpace(Speaker) ? Speaker : SpeakerKey;

    private static string FormatTime(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1
            ? value.ToString(@"h\:mm\:ss")
            : value.ToString(@"m\:ss");
    }
}

public sealed class TranscriptDocument
{
    public long Id { get; init; }
    public long EpisodeId { get; init; }
    public TranscriptStatus Status { get; init; } = TranscriptStatus.Complete;
    public string Language { get; init; } = "";
    public string EngineId { get; init; } = "";
    public string EngineVersion { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string Source { get; init; } = "local";
    public string FullText { get; init; } = "";
    public int WordCount { get; init; }
    public long DurationMs { get; init; }
    public bool HasWordTimings { get; init; }
    public bool HasSpeakerDiarization { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; init; }
    public int Revision { get; init; } = 1;
    public string MetadataJson { get; init; } = "{}";
    public IReadOnlyList<TranscriptSegment> Segments { get; init; } = Array.Empty<TranscriptSegment>();
    public IReadOnlyList<TranscriptSpeakerCluster> Speakers { get; init; } = Array.Empty<TranscriptSpeakerCluster>();
}

public sealed class TranscriptSummary
{
    public long TranscriptId { get; init; }
    public long EpisodeId { get; init; }
    public string Show { get; init; } = "";
    public DateTime? AirDate { get; init; }
    public string EpisodeTitle { get; init; } = "";
    public TranscriptStatus Status { get; init; }
    public string Language { get; init; } = "";
    public string EngineId { get; init; } = "";
    public string ModelId { get; init; } = "";
    public string Source { get; init; } = "";
    public int WordCount { get; init; }
    public int SegmentCount { get; init; }
    public int SpeakerCount { get; init; }
    public int IdentifiedSpeakerCount { get; init; }
    public long DurationMs { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }

    public string AirDateDisplay => AirDate?.ToString("dd MMM yyyy") ?? "Unknown date";
    public string EngineDisplay => string.IsNullOrWhiteSpace(ModelId)
        ? (string.IsNullOrWhiteSpace(EngineId) ? Source : EngineId)
        : $"{EngineId} · {ModelId}";
    public string SizeDisplay => $"{WordCount:N0} words · {SegmentCount:N0} segments";
    public string SpeakersDisplay => SpeakerCount == 0
        ? "No speaker labels"
        : IdentifiedSpeakerCount == SpeakerCount
            ? $"{SpeakerCount:N0} identified"
            : $"{IdentifiedSpeakerCount:N0}/{SpeakerCount:N0} identified";
    public string UpdatedDisplay => UpdatedAt.LocalDateTime.ToString("dd MMM yyyy HH:mm");
}

public sealed record TranscriptEpisodeIdentity(
    long EpisodeId,
    string BroadcastUid,
    string Show,
    DateTime? AirDate,
    int PartNumber,
    string Title);

public sealed record TranscriptionContext(
    string Show,
    string Title,
    IReadOnlyList<string> People,
    IReadOnlyList<string> Topics,
    IReadOnlyList<string> Terms)
{
    [JsonIgnore]
    public string Prompt => string.Join(", ", Terms
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(80));
}

public sealed record TranscriptionRequest(
    long EpisodeId,
    string AudioPath,
    string Language,
    string ModelId,
    long? ExpectedDurationMs = null,
    string? WorkingDirectory = null,
    long StartMs = 0,
    long? DurationMs = null,
    bool EnableSpeakerDiarization = false,
    bool UseVoiceActivityDetection = false,
    string ContextPrompt = "",
    long? InputOffsetMs = null,
    Guid OperationId = default)
{
    [JsonIgnore]
    public long EffectiveDurationMs => DurationMs ?? ExpectedDurationMs ?? 0;
}

public sealed record TranscriptionJobOptions(
    string Language = "",
    string ModelId = "",
    long StartMs = 0,
    long? DurationMs = null,
    bool EnableSpeakerDiarization = false,
    bool UseVoiceActivityDetection = false,
    bool ReplaceExistingTranscript = false)
{
    [JsonIgnore]
    public bool IsPartial => StartMs > 0 || DurationMs.HasValue;

    [JsonIgnore]
    public string RangeDisplay
    {
        get
        {
            if (!IsPartial) return "Full broadcast";
            var start = TimeSpan.FromMilliseconds(Math.Max(0, StartMs));
            var duration = TimeSpan.FromMilliseconds(Math.Max(0, DurationMs ?? 0));
            var startText = start.TotalHours >= 1 ? start.ToString(@"h\:mm\:ss") : start.ToString(@"m\:ss");
            var durationText = duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss") : duration.ToString(@"m\:ss");
            return $"{durationText} from {startText}";
        }
    }
}

public sealed record TranscriptionEngineProgress(
    double? Percent,
    string Message,
    long ProcessedMs = 0,
    long TotalMs = 0);

public sealed record TranscriptionResult(
    string Language,
    string FullText,
    long DurationMs,
    IReadOnlyList<TranscriptSegment> Segments,
    string EngineId,
    string EngineVersion,
    string ModelId,
    bool HasWordTimings,
    bool HasSpeakerDiarization = false,
    string MetadataJson = "{}");

public sealed class TranscriptionJobRecord
{
    public Guid JobId { get; init; }
    public long EpisodeId { get; init; }
    public TranscriptionJobState State { get; init; }
    public string EngineId { get; init; } = "";
    public string ModelId { get; init; } = "";
    public double? ProgressPercent { get; init; }
    public string Message { get; init; } = "";
    public string Error { get; init; } = "";
    public DateTimeOffset RequestedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public Guid? BackgroundJobId { get; init; }
    public string Language { get; init; } = "";
    public long StartMs { get; init; }
    public long? DurationMs { get; init; }
    public bool EnableSpeakerDiarization { get; init; }
    public bool UseVoiceActivityDetection { get; init; }
    public bool ReplaceExistingTranscript { get; init; }
    public bool IsPaused { get; init; }

    public string ProgressDisplay => ProgressPercent.HasValue ? $"{ProgressPercent.Value:0}%" : "—";
    public string StateDisplay => IsPaused ? "Paused" : State.ToString();
    public string RequestedDisplay => RequestedAt.LocalDateTime.ToString("dd MMM yyyy HH:mm");
    public string RangeDisplay => new TranscriptionJobOptions(StartMs: StartMs, DurationMs: DurationMs).RangeDisplay;
    public bool CanRetry => State is TranscriptionJobState.Failed or TranscriptionJobState.Cancelled or TranscriptionJobState.Interrupted;
    public bool CanPause => State == TranscriptionJobState.Running && !IsPaused;
    public bool CanResume => State == TranscriptionJobState.Running && IsPaused;
    public bool CanCancel => State is TranscriptionJobState.Queued or TranscriptionJobState.Running;
}

public sealed class TranscriptPackage
{
    public const int CurrentFormatVersion = 3;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public string PackageId { get; init; } = Guid.NewGuid().ToString("D");
    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;
    public string ExportedBy { get; init; } = "Radio Vault";
    public TranscriptEpisodeIdentity Episode { get; init; } = new(0, "", "", null, 1, "");
    public TranscriptDocument Transcript { get; init; } = new();
}

public sealed record TranscriptImportResult(
    long EpisodeId,
    long TranscriptId,
    int Revision,
    int SegmentCount,
    int WordCount,
    bool ReplacedExisting,
    string PackageId);
