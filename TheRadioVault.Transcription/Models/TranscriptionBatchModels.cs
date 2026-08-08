namespace TheRadioVault.Transcription.Models;

public enum TranscriptionBatchState
{
    Queued,
    Running,
    Paused,
    Completed,
    CompletedWithErrors,
    Cancelled,
    Interrupted
}

public enum TranscriptionBatchItemState
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped,
    Cancelled
}

public sealed record TranscriptionBatchCandidate(
    long EpisodeId,
    string Show,
    DateOnly? AirDate,
    string Title,
    long DurationMs,
    bool HasTranscript)
{
    public string DateDisplay => AirDate?.ToString("dd MMM yyyy") ?? "Unknown date";
    public string SelectionState => HasTranscript ? "Already transcribed · will skip" : "Ready";
}

public sealed record TranscriptionBatchCreateRequest(
    string Name,
    string SelectionLabel,
    TranscriptionJobOptions Options,
    IReadOnlyList<TranscriptionBatchCandidate> Candidates);

public sealed class TranscriptionBatchRecord
{
    public Guid BatchId { get; init; }
    public string Name { get; init; } = "";
    public string SelectionLabel { get; init; } = "";
    public TranscriptionBatchState State { get; init; }
    public string Language { get; init; } = "";
    public string ModelId { get; init; } = "";
    public bool EnableSpeakerDiarization { get; init; }
    public bool UseVoiceActivityDetection { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public int TotalCount { get; init; }
    public int PendingCount { get; init; }
    public int RunningCount { get; init; }
    public int CompletedCount { get; init; }
    public int FailedCount { get; init; }
    public int SkippedCount { get; init; }
    public int CancelledCount { get; init; }
    public double CurrentJobPercent { get; init; }

    public bool CanPause => State == TranscriptionBatchState.Running;
    public bool CanResume => State is TranscriptionBatchState.Paused or TranscriptionBatchState.Interrupted;
    public bool CanCancel => State is TranscriptionBatchState.Queued or TranscriptionBatchState.Running or TranscriptionBatchState.Paused or TranscriptionBatchState.Interrupted;
    public bool CanRetryFailed => FailedCount > 0 && State is not TranscriptionBatchState.Running;
    public int FinishedCount => CompletedCount + FailedCount + SkippedCount + CancelledCount;
    public double ProgressPercent => TotalCount <= 0
        ? 0
        : Math.Clamp(((CompletedCount + FailedCount + SkippedCount + CancelledCount) * 100d + CurrentJobPercent) / TotalCount, 0, 100);
    public string ProgressDisplay => $"{ProgressPercent:0}%";
    public string CountDisplay => $"{CompletedCount:N0} complete · {PendingCount + RunningCount:N0} remaining";
    public string IssueDisplay => FailedCount > 0
        ? $"{FailedCount:N0} failed · {SkippedCount:N0} skipped"
        : SkippedCount > 0 ? $"{SkippedCount:N0} skipped" : "No failures";
    public string CreatedDisplay => CreatedAt.LocalDateTime.ToString("dd MMM yyyy HH:mm");
    public string EstimateDisplay
    {
        get
        {
            if (State == TranscriptionBatchState.Paused) return "Paused · estimate will resume with the batch";
            if (State is TranscriptionBatchState.Completed or TranscriptionBatchState.CompletedWithErrors or TranscriptionBatchState.Cancelled)
                return $"Finished {FinishedAt?.LocalDateTime.ToString("dd MMM HH:mm") ?? ""}".Trim();
            var measured = CompletedCount + FailedCount;
            var remaining = PendingCount + RunningCount;
            if (!StartedAt.HasValue || measured == 0 || remaining == 0) return "Estimating time remaining…";
            var elapsed = DateTimeOffset.UtcNow - StartedAt.Value;
            var estimate = TimeSpan.FromTicks(Math.Max(0, elapsed.Ticks / measured * remaining));
            return estimate.TotalHours >= 1
                ? $"About {Math.Ceiling(estimate.TotalHours):N0} hours remaining"
                : $"About {Math.Max(1, Math.Ceiling(estimate.TotalMinutes)):N0} minutes remaining";
        }
    }
}

public sealed class TranscriptionBatchItemRecord
{
    public long Id { get; init; }
    public Guid BatchId { get; init; }
    public long EpisodeId { get; init; }
    public int Position { get; init; }
    public TranscriptionBatchItemState State { get; init; }
    public Guid? TranscriptionJobId { get; init; }
    public string Error { get; init; } = "";
    public string Show { get; init; } = "";
    public DateOnly? AirDate { get; init; }
    public string Title { get; init; } = "";
    public long DurationMs { get; init; }
    public double? ProgressPercent { get; init; }
    public string JobMessage { get; init; } = "";

    public string DateDisplay => AirDate?.ToString("dd MMM yyyy") ?? "Unknown date";
    public string PositionDisplay => $"#{Position + 1:N0}";
    public string ProgressDisplay => State == TranscriptionBatchItemState.Running && ProgressPercent.HasValue
        ? $"{ProgressPercent.Value:0}%"
        : State.ToString();
    public string DetailDisplay => !string.IsNullOrWhiteSpace(Error) ? Error
        : !string.IsNullOrWhiteSpace(JobMessage) ? JobMessage
        : State == TranscriptionBatchItemState.Skipped ? "Transcript already exists" : State.ToString();
    public bool CanReorder => State == TranscriptionBatchItemState.Pending;
}
