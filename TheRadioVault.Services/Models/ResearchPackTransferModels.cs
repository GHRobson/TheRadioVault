namespace TheRadioVault.Services.Models;

public sealed record ResearchPackPreviewSummary(
    string PackageName,
    string Show,
    int BroadcastCount,
    int TranscriptCount,
    int MatchedCount,
    int MissingCount,
    int AmbiguousCount,
    bool AuthoritativeAudit,
    string SummaryText,
    int WikiPageCount = 0,
    int WikiImageCount = 0,
    int WikiTimelineEventCount = 0);

public sealed record ResearchPackApplySummary(
    int ImportedCount,
    int MatchedCount,
    int MissingCount,
    int ConflictCount,
    string SummaryText,
    int WikiPagesChanged = 0,
    int WikiConflicts = 0);

public sealed record ResearchPackTransferProgress(
    double Percent,
    string Message,
    int Current = 0,
    int Total = 0,
    string Phase = "Import")
{
    public double ClampedPercent => Math.Clamp(Percent, 0, 100);
    public string CountText => Total > 0 ? $"{Current:N0} of {Total:N0}" : string.Empty;
}

public sealed record ResearchPackExportSummary(
    byte[] Bytes,
    string SuggestedFileName,
    int BroadcastCount,
    int MissingCount,
    int TranscriptCount,
    int WikiPageCount = 0);
