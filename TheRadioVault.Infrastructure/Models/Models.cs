namespace TheRadioVault.Models;

public sealed class Collection
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
}

public sealed class CollectionSummary
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public int EpisodeCount { get; set; }
}

public sealed class LibraryFolder
{
    public int Id { get; set; }
    public string Path { get; set; } = "";
    public int? AssignedCollectionId { get; set; }
    public bool Enabled { get; set; } = true;
    public bool Recursive { get; set; } = true;
    public DateTime? LastScanAt { get; set; }
    public string CollectionName { get; set; } = "Auto detect";
    public string Display => $"{Path}  ·  {CollectionName}";
}

public enum EpisodeStorageState
{
    AvailableOffline,
    CloudOnly,
    Downloading,
    Missing
}

public sealed class EpisodeListItem : System.ComponentModel.INotifyPropertyChanged
{
    public long Id { get; set; }
    public string CanonicalKey { get; set; } = "";
    public bool IsCanonicalBroadcast => !string.IsNullOrWhiteSpace(CanonicalKey);
    public string LibraryIdentityKey => IsCanonicalBroadcast ? CanonicalKey : $"EPISODE:{Id}";
    public int RecordingCount { get; set; } = 1;
    public int SegmentCount { get; set; } = 1;
    public int PhysicalFileCount { get; set; } = 1;
    public bool NeedsAttention { get; set; }
    public string AttentionState { get; set; } = "";
    public string AttentionReason { get; set; } = "";
    public string CanonicalStructureDisplay
    {
        get
        {
            var parts = new List<string>();
            if (SegmentCount > 1) parts.Add($"{SegmentCount:N0} parts");
            if (RecordingCount > 1) parts.Add($"{RecordingCount:N0} recordings");
            if (PhysicalFileCount > 1) parts.Add($"{PhysicalFileCount:N0} files");
            return string.Join(" · ", parts);
        }
    }
    public string BroadcastUid { get; set; } = "";
    public int PartNumber { get; set; } = 1;
    public int? TotalParts { get; set; }
    public bool IsMultipart => (TotalParts ?? 0) > 1 || PartNumber > 1;
    public string MultipartDisplay => TotalParts is > 1
        ? $"Part {PartNumber} of {TotalParts}"
        : PartNumber > 1 ? $"Part {PartNumber}" : "";
    public string MultipartGroupDisplay => IsMultipart ? $"Linked recording · {MultipartDisplay}" : "";
    public string BroadcastGroupKey => $"{CollectionId}|{AirDate:yyyy-MM-dd}|{BroadcastSlot}";
    public string BroadcastSlot { get; set; } = "";
    public string Edition { get; set; } = "";
    public int MetadataConfidence { get; set; }
    public string MetadataConfidenceReason { get; set; } = "";
    public string EditionDisplay => string.IsNullOrWhiteSpace(Edition) ? "" : Edition;
    public string MetadataConfidenceDisplay => $"{MetadataConfidence}% metadata confidence";
    public int CollectionId { get; set; }
    public string CollectionName { get; set; } = "";
    public DateTime? AirDate { get; set; }
    public string AirDateDisplay => AirDate?.ToString("dd MMM yyyy") ?? "Unknown";
    public string AirDateAndSlotDisplay => string.IsNullOrWhiteSpace(BroadcastSlot) ? AirDateDisplay : $"{AirDateDisplay} · {BroadcastSlot}";
    // Stored in the existing database Title column for backwards compatibility,
    // but presented to users as an optional broadcast Headline.
    public string DisplayTitle { get; set; } = "";
    public string Headline
    {
        get => TheRadioVault.Core.Services.TitleQualityService.DisplayHeadline(DisplayTitle, CollectionName, OriginalFilename);
        set => DisplayTitle = value ?? "";
    }
    public bool HasMeaningfulTitle => !string.IsNullOrWhiteSpace(Headline);
    public bool HasHeadline => HasMeaningfulTitle;
    public string SmartTitle => Headline;
    public string DashboardTitle => TheRadioVault.Core.Services.TitleQualityService.DashboardTitle(DisplayTitle, CollectionName, OriginalFilename, AirDate);
    public string DashboardSubtitle => HasMeaningfulTitle ? AirDateDisplay : CollectionName;
    private string? _dashboardActionOverride;
    public string DashboardActionLabel
    {
        get => _dashboardActionOverride ?? TheRadioVault.Core.Services.PlaybackProgressService.GetDashboardAction(PositionMs, DurationMs, Status == "Completed");
    }
    public void SetDashboardAction(string? label)
    {
        _dashboardActionOverride = label;
        OnPropertyChanged(nameof(DashboardActionLabel));
    }
    public string OriginalFilename { get; set; } = "";
    public string Path { get; set; } = "";
    private string _status = "Unplayed";
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged(nameof(Status));
            OnPropertyChanged(nameof(DashboardActionLabel));
            OnPropertyChanged(nameof(ListeningSummary));
        }
    }

    private long _positionMs;
    public long PositionMs
    {
        get => _positionMs;
        set
        {
            if (_positionMs == value) return;
            _positionMs = value;
            OnPropertyChanged(nameof(PositionMs));
            OnPropertyChanged(nameof(ProgressDisplay));
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(RemainingDisplay));
            OnPropertyChanged(nameof(ListeningSummary));
            OnPropertyChanged(nameof(DashboardActionLabel));
        }
    }

    private long _durationMs;
    public long DurationMs
    {
        get => _durationMs;
        set
        {
            if (_durationMs == value) return;
            _durationMs = value;
            OnPropertyChanged(nameof(DurationMs));
            OnPropertyChanged(nameof(ProgressDisplay));
            OnPropertyChanged(nameof(ProgressPercent));
            OnPropertyChanged(nameof(RemainingDisplay));
            OnPropertyChanged(nameof(DurationDisplay));
            OnPropertyChanged(nameof(ListeningSummary));
        }
    }
    public DateTime? LastPlayedAt { get; set; }
    public DateTime DateAdded { get; set; }
    public bool Favourite { get; set; }
    public string FavouriteDisplay => Favourite ? "★" : "☆";

    private bool _isCurrentPlayback;
    /// <summary>True when this row represents the broadcast loaded by the player.</summary>
    public bool IsCurrentPlayback
    {
        get => _isCurrentPlayback;
        set
        {
            if (_isCurrentPlayback == value) return;
            _isCurrentPlayback = value;
            OnPropertyChanged(nameof(IsCurrentPlayback));
            OnPropertyChanged(nameof(PlaybackIndicator));
        }
    }

    private bool _isPlaybackPaused;
    /// <summary>Distinguishes a paused current broadcast from an actively playing one.</summary>
    public bool IsPlaybackPaused
    {
        get => _isPlaybackPaused;
        set
        {
            if (_isPlaybackPaused == value) return;
            _isPlaybackPaused = value;
            OnPropertyChanged(nameof(IsPlaybackPaused));
            OnPropertyChanged(nameof(PlaybackIndicator));
        }
    }

    public string PlaybackIndicator => !IsCurrentPlayback ? string.Empty : IsPlaybackPaused ? "Ⅱ" : "▶";
    public int? Year => AirDate?.Year;
    public string MonthDisplay => AirDate?.ToString("MMMM") ?? "Unknown";
    public string Hosts { get; set; } = "";
    public string Guests { get; set; } = "";
    public string Callers { get; set; } = "";
    public string MentionedPeople { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Summary { get; set; } = "";
    public string SummaryTeaser => TheRadioVault.Services.EpisodePresentationService.SummaryTeaser(Summary);
    public string DiscoveryLine => TheRadioVault.Services.EpisodePresentationService.DiscoveryLine(Guests, Tags, Edition, BroadcastSlot);
    public string ContextBadge => TheRadioVault.Services.EpisodePresentationService.ContextBadge(Edition, BroadcastSlot, IsMultipart ? MultipartDisplay : "");
    private string? _artworkPath;
    public string? ArtworkPath
    {
        get => _artworkPath;
        set
        {
            if (string.Equals(_artworkPath, value, StringComparison.OrdinalIgnoreCase)) return;
            _artworkPath = value;
            OnPropertyChanged(nameof(ArtworkPath));
        }
    }
    private EpisodeStorageState _storageState = EpisodeStorageState.AvailableOffline;
    public EpisodeStorageState StorageState
    {
        get => _storageState;
        set
        {
            if (_storageState == value) return;
            _storageState = value;
        }
    }
    public string StorageStateDisplay => StorageState switch
    {
        EpisodeStorageState.CloudOnly => "Cloud only",
        EpisodeStorageState.Downloading => "Downloading…",
        EpisodeStorageState.Missing => "Unavailable",
        _ => "Available offline"
    };
    public string ProgressDisplay => DurationMs > 0
        ? $"{Math.Clamp((double)PositionMs / DurationMs * 100, 0, 100):0}%"
        : PositionMs > 0 ? "Started" : "—";
    public double ProgressPercent => TheRadioVault.Core.Services.PlaybackProgressService.CalculatePercent(PositionMs, DurationMs);
    public string DurationDisplay
    {
        get
        {
            if (DurationMs <= 0) return "Duration unknown";
            var duration = TimeSpan.FromMilliseconds(DurationMs);
            return duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours}h {duration.Minutes:00}m"
                : $"{duration.Minutes}m {duration.Seconds:00}s";
        }
    }
    public string DayDisplay => AirDate?.ToString("dddd") ?? "Date unknown";
    public string BrowserMetadata
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Guests)) parts.Add(Guests);
            if (!string.IsNullOrWhiteSpace(Tags)) parts.Add(Tags);
            return parts.Count > 0 ? string.Join("  ·  ", parts) : "Archive episode";
        }
    }
    public string BrowserMetadataClean
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Guests)) parts.Add(Guests);
            if (!string.IsNullOrWhiteSpace(Tags)) parts.Add(Tags);
            return string.Join("  ·  ", parts);
        }
    }
    public string RemainingDisplay
    {
        get
        {
            if (DurationMs <= 0 || PositionMs <= 0 || PositionMs >= DurationMs) return DurationDisplay;
            var remaining = TimeSpan.FromMilliseconds(DurationMs - PositionMs);
            return remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}h {remaining.Minutes:00}m remaining"
                : $"{remaining.Minutes}m remaining";
        }
    }
    public string ListeningSummary => $"{Status}  ·  {ProgressDisplay}  ·  {DurationDisplay}";

    /// <summary>
    /// Notifies WPF that a server synchronization updated several snapshot-backed
    /// values at once. An empty property name is the standard all-properties
    /// notification and avoids replacing thousands of row objects for a small LAN
    /// delta such as progress, favourite or metadata changes.
    /// </summary>
    public void NotifySnapshotChanged() => OnPropertyChanged(string.Empty);

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged(string propertyName)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
}

public sealed class PlaybackState
{
    public long EpisodeId { get; set; }
    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public bool Completed { get; set; }
    public double PlaybackSpeed { get; set; } = 1.0;
    public DateTime? FirstPlayedAt { get; set; }
    public DateTime? LastPlayedAt { get; set; }
    public int PlayCount { get; set; }
    public int CompletionCount { get; set; }
}

public sealed class ScanResult
{
    public int FilesFound { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Unchanged { get; set; }
    public int Errors { get; set; }

    // Alpha6 triages research after scanning: deterministic identity matches may
    // be applied automatically, while ResearchAmbiguous counts grouped decisions
    // that genuinely require a person. No audio-file operation is performed.
    public int ResearchApplied { get; set; }
    public int ResearchAmbiguous { get; set; }
    public bool ResearchTriageFailed { get; set; }
    public int ResearchCandidatesFound { get; set; }
    public int PreviouslyMissingMatches { get; set; }
    public int AlternateCaptureCandidates { get; set; }

    // Alpha 5 Buildfix 5 keeps post-cutover scans inside the canonical library.
    // These counters describe the incremental canonical promotion performed
    // after physical files have been indexed.
    public int CanonicalBroadcastsAdded { get; set; }
    public int CanonicalRecordingsAdded { get; set; }
    public int CanonicalEpisodesMapped { get; set; }
    public int CanonicalItemsNeedingReview { get; set; }
}


public sealed class EpisodeMetadata
{
    public long EpisodeId { get; set; }
    public string Title { get; set; } = ""; // Database compatibility: this is the optional Headline.
    public string Headline { get => Title; set => Title = value ?? ""; }
    public string Description { get; set; } = "";
    public string Notes { get; set; } = "";
    public string ArchiveNotes { get; set; } = "";
    public string Edition { get; set; } = "";
    public int MetadataConfidence { get; set; }
    public string MetadataConfidenceReason { get; set; } = "";
    public string Hosts { get; set; } = "";
    public string Guests { get; set; } = "";
    public string Callers { get; set; } = "";
    public string MentionedPeople { get; set; } = "";
    public string Tags { get; set; } = "";
    public string Summary { get; set; } = "";
    public string SummaryTeaser => TheRadioVault.Services.EpisodePresentationService.SummaryTeaser(Summary);
    public string? ArtworkPath { get; set; }
    private EpisodeStorageState _storageState = EpisodeStorageState.AvailableOffline;
    public EpisodeStorageState StorageState
    {
        get => _storageState;
        set
        {
            if (_storageState == value) return;
            _storageState = value;
        }
    }
    public string StorageStateDisplay => StorageState switch
    {
        EpisodeStorageState.CloudOnly => "Cloud only",
        EpisodeStorageState.Downloading => "Downloading…",
        EpisodeStorageState.Missing => "Unavailable",
        _ => "Available offline"
    };
    public bool UserModified { get; set; }
}

public sealed class ScannedAudioMetadata
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string[] Guests { get; set; } = Array.Empty<string>();
    public string[] Tags { get; set; } = Array.Empty<string>();
    public long DurationMs { get; set; }
    public byte[]? ArtworkBytes { get; set; }
    public string? ArtworkMimeType { get; set; }
}

public sealed class MetadataExportEpisode
{
    public string Collection { get; set; } = "";
    public string? AirDate { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Notes { get; set; } = "";
    public string ArchiveNotes { get; set; } = "";
    public string Edition { get; set; } = "";
    public string[] Guests { get; set; } = Array.Empty<string>();
    public string[] Tags { get; set; } = Array.Empty<string>();
    public string OriginalFilename { get; set; } = "";
}

public sealed class MetadataPackage
{
    public string Format { get; set; } = "theradiovault.metadata-package";
    public int SchemaVersion { get; set; } = 2;
    public string AppVersion { get; set; } = "";
    public DateTime ExportedUtc { get; set; } = DateTime.UtcNow;
    public List<MetadataPackageEpisode> Episodes { get; set; } = new();
}

public sealed class MetadataPackageEpisode
{
    public string BroadcastUid { get; set; } = "";
    public string Collection { get; set; } = "";
    public string? AirDate { get; set; }
    public int PartNumber { get; set; } = 1;
    public int? TotalParts { get; set; }
    public string OriginalFilename { get; set; } = "";
    public string Headline { get; set; } = "";
    public string Summary { get; set; } = "";
    public string PersonalNotes { get; set; } = "";
    public string ArchiveNotes { get; set; } = "";
    public MetadataBroadcastFields Broadcast { get; set; } = new();
    public MetadataPeopleFields People { get; set; } = new();
    public string[] Topics { get; set; } = Array.Empty<string>();
    public MetadataResearchFields Research { get; set; } = new();
    public MetadataArchiveFields Archive { get; set; } = new();
    public List<MetadataPackageMoment> Moments { get; set; } = new();
}

public sealed class MetadataBroadcastFields
{
    public string Station { get; set; } = "";
    public string Slot { get; set; } = "";
    public string Variant { get; set; } = "";
    public string Era { get; set; } = "";
    public string EpisodeType { get; set; } = "";
}

public sealed class MetadataPeopleFields
{
    public string[] Hosts { get; set; } = Array.Empty<string>();
    public string[] Guests { get; set; } = Array.Empty<string>();
    public string[] Callers { get; set; } = Array.Empty<string>();
    public string[] MentionedPeople { get; set; } = Array.Empty<string>();
}

public sealed class MetadataResearchFields
{
    public int Confidence { get; set; }
    public string ConfidenceReason { get; set; } = "";
    public string[] Sources { get; set; } = Array.Empty<string>();
}

public sealed class MetadataArchiveFields
{
    public string ArtworkPath { get; set; } = "";
    public long DurationMs { get; set; }
    public string StorageState { get; set; } = "";
    public bool Favourite { get; set; }
}

public sealed class MetadataPackageMoment
{
    public long PositionMs { get; set; }
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime? CreatedUtc { get; set; }
}

public sealed class MetadataImportReport
{
    public int Total { get; set; }
    public int Matched { get; set; }
    public int Updated { get; set; }
    public int Unmatched { get; set; }
    public int MomentsAdded { get; set; }
    public int Ambiguous { get; set; }
}

public sealed class ArtworkItem { public long EpisodeId { get; set; } public string Title { get; set; } = ""; public string CollectionName { get; set; } = ""; public string ArtworkPath { get; set; } = ""; }


public sealed class ScanHistoryItem
{
    public long Id { get; set; }
    public DateTime StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string ScanType { get; set; } = "Full";
    public int FilesFound { get; set; }
    public int FilesAdded { get; set; }
    public int FilesUpdated { get; set; }
    public int FilesUnchanged { get; set; }
    public int MissingFiles { get; set; }
    public int Errors { get; set; }
    public string Summary => $"{StartedAt:g} · {FilesFound:N0} found · {FilesAdded:N0} added · {FilesUpdated:N0} updated · {FilesUnchanged:N0} unchanged · {MissingFiles:N0} missing · {Errors:N0} errors";
}

public sealed class MissingFileItem
{
    public long MediaFileId { get; set; }
    public long EpisodeId { get; set; }
    public string CollectionName { get; set; } = "";
    public string EpisodeTitle { get; set; } = "";
    public string OriginalFilename { get; set; } = "";
    public string PreviousPath { get; set; } = "";
    public long FileSize { get; set; }
}

public sealed class DuplicateGroupItem
{
    public long EpisodeId { get; set; }
    public string CollectionName { get; set; } = "";
    public string AirDate { get; set; } = "Unknown";
    public string EpisodeTitle { get; set; } = "";
    public string Filename { get; set; } = "";
    public string Path { get; set; } = "";
    public long DurationMs { get; set; }
    public string GroupKey { get; set; } = "";
}

public sealed class LibraryHealthSummary
{
    public int MissingFiles { get; set; }
    public int DuplicateCandidates { get; set; }
    public int NeedsReview { get; set; }
    public int LibraryFolders { get; set; }
}


public sealed class MomentItem
{
    public long Id { get; set; }
    public long EpisodeId { get; set; }
    public string CollectionName { get; set; } = "";
    public string EpisodeTitle { get; set; } = "";
    public string AirDateDisplay { get; set; } = "Unknown";
    public long PositionMs { get; set; }
    public string PositionDisplay => TimeSpan.FromMilliseconds(PositionMs).TotalHours >= 1
        ? TimeSpan.FromMilliseconds(PositionMs).ToString(@"hh\:mm\:ss")
        : TimeSpan.FromMilliseconds(PositionMs).ToString(@"mm\:ss");
    public string Title { get; set; } = "";
    public string Notes { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}


public sealed class QueueItem
{
    public long QueueId { get; set; }
    public int Position { get; set; }
    public long EpisodeId { get; set; }
    public string CollectionName { get; set; } = "";
    public string DisplayTitle { get; set; } = "";
    public string OriginalFilename { get; set; } = "";
    public string Path { get; set; } = "";
    public DateTime? AirDate { get; set; }
    public string BroadcastSlot { get; set; } = "";
    public string AirDateDisplay => AirDate?.ToString("dd MMM yyyy") ?? "Unknown";
    public string AirDateAndSlotDisplay => string.IsNullOrWhiteSpace(BroadcastSlot) ? AirDateDisplay : $"{AirDateDisplay} · {BroadcastSlot}";
    public string SmartTitle
    {
        get
        {
            var headline = TheRadioVault.Core.Services.TitleQualityService.DisplayHeadline(DisplayTitle, CollectionName, OriginalFilename);
            return string.IsNullOrWhiteSpace(headline) ? CollectionName : headline;
        }
    }
    public string QueueSubtitle => $"{CollectionName} · {AirDateDisplay}";
}


public sealed class ArchivePeriodCard
{
    public int Value { get; set; }
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string ShowsText { get; set; } = "";
    public string ProgressText { get; set; } = "";
    public double ProgressPercent { get; set; }
    public string ArtworkPath { get; set; } = "";
}


public sealed class StorageSummary
{
    public int TotalFiles { get; set; }
    public int AvailableOffline { get; set; }
    public int CloudOnly { get; set; }
    public int Missing { get; set; }
    public long LogicalBytes { get; set; }
    public string LogicalSizeDisplay => FormatBytes(LogicalBytes);
    private static string FormatBytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}

public sealed class FileSyncOptions { public bool RenameFiles { get; set; } = true; public bool WriteTags { get; set; } = true; public bool EmbedArtwork { get; set; } public bool CreateUndoManifest { get; set; } = true; }
public sealed class FileSyncPreviewItem { public long EpisodeId { get; set; } public string BroadcastUid { get; set; } = ""; public string CurrentPath { get; set; } = ""; public string ProposedPath { get; set; } = ""; public string CollectionName { get; set; } = ""; public DateTime? AirDate { get; set; } public string Title { get; set; } = ""; public string? ArtworkPath { get; set; } public bool WillRename => !string.Equals(CurrentPath, ProposedPath, StringComparison.OrdinalIgnoreCase); }

public sealed class HeadlineReviewItem
{
    public long EpisodeId { get; set; }
    public string CollectionName { get; set; } = "";
    public DateTime? AirDate { get; set; }
    public string AirDateDisplay => AirDate?.ToString("dddd, d MMMM yyyy") ?? "Date unknown";
    public string Candidate { get; set; } = "";
    public string SmartTitle => Candidate;
    public string Confidence { get; set; } = "Probable";
    public string Reasoning { get; set; } = "";
    public string OriginalFilename { get; set; } = "";
    public string Path { get; set; } = "";
    public string BroadcastUid { get; set; } = "";
    public string CurrentHeadline { get; set; } = "";
    public bool IsAlreadyApplied { get; set; }
    public bool WasUserModified { get; set; }
    public string ParserVersion { get; set; } = "";
    public string PreviousDecision { get; set; } = "";
    public string ReviewStatus => IsAlreadyApplied ? "Provisionally applied from filename — review to confirm" : PreviousDecision == "Skipped" ? "Previously skipped — available for later review" : "Suggested from filename — review before applying";
}


public sealed class GuestBadge
{
    public string Name { get; set; } = "";
    public string Initials
    {
        get
        {
            var parts = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length switch
            {
                0 => "?",
                1 => parts[0][..Math.Min(1, parts[0].Length)].ToUpperInvariant(),
                _ => string.Concat(parts.Take(2).Select(x => char.ToUpperInvariant(x[0])))
            };
        }
    }
}

public sealed class BroadcastKnowledge
{
    public long EpisodeId { get; set; }
    public string BroadcastUid { get; set; } = "";
    public string CollectionName { get; set; } = "";
    public DateTime? AirDate { get; set; }
    public string BroadcastSlot { get; set; } = "";
    public int PartNumber { get; set; } = 1;
    public int? TotalParts { get; set; }
    public string Headline { get; set; } = "";
    public string Summary { get; set; } = "";
    public string ArchiveNotes { get; set; } = "";
    public string PersonalNotes { get; set; } = "";
    public string Edition { get; set; } = "";
    public string ArtworkPath { get; set; } = "";
    public List<string> Hosts { get; set; } = new();
    public List<string> Guests { get; set; } = new();
    public List<string> Callers { get; set; } = new();
    public List<string> MentionedPeople { get; set; } = new();
    public List<string> Topics { get; set; } = new();
    public List<RelatedBroadcastItem> Related { get; set; } = new();
    public string AirDateDisplay => AirDate?.ToString("dddd, d MMMM yyyy") ?? "Date unknown";
    public string IdentitySubtitle
    {
        get
        {
            var bits = new List<string> { AirDateDisplay };
            if (!string.IsNullOrWhiteSpace(BroadcastSlot)) bits.Add(BroadcastSlot);
            if (TotalParts is > 1) bits.Add($"Part {PartNumber} of {TotalParts}");
            if (!string.IsNullOrWhiteSpace(Edition)) bits.Add(Edition);
            return string.Join(" · ", bits);
        }
    }
}

public sealed class RelatedBroadcastItem
{
    public long EpisodeId { get; set; }
    public string Label { get; set; } = "";
    public string Subtitle { get; set; } = "";
}

public sealed class TrvPackManifest
{
    public string Format { get; set; } = "radiovault.archive-knowledge-database";
    public int SchemaVersion { get; set; } = 1;
    public string CreatedBy { get; set; } = "The Radio Vault";
    public string AppVersion { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public string Show { get; set; } = "";
    public string ExportScope { get; set; } = "complete";
    public int? Year { get; set; }
    public int BroadcastCount { get; set; }
    public int MissingBroadcastCount { get; set; }
    public int TranscriptCount { get; set; }
    public int WikiPageCount { get; set; }
    public int WikiImageCount { get; set; }
    public int WikiTimelineEventCount { get; set; }
    public string Purpose { get; set; } = "Unified Research, transcript and Wiki knowledge exchange";
}

public sealed class TrvPackBroadcast
{
    public string BroadcastId { get; set; } = "";
    public string Show { get; set; } = "";
    public string? BroadcastDate { get; set; }
    public string? Slot { get; set; }
    public int PartNumber { get; set; } = 1;
    public int? TotalParts { get; set; }
    public TrvPackResearch Research { get; set; } = new();
    public List<TrvPackSource> Sources { get; set; } = new();
    public TrvPackImportPolicy ImportPolicy { get; set; } = new();
}

public sealed class TrvPackResearch
{
    public string? Headline { get; set; }
    public string? Summary { get; set; }

    // Structured Research Engine 2.0 fields.
    public TrvPackBroadcastMetadata Broadcast { get; set; } = new();
    public TrvPackPeople People { get; set; } = new();
    public List<string> Topics { get; set; } = new();
    public TrvPackResearchQuality Quality { get; set; } = new();
    public TrvPackCatalogueMetadata Catalogue { get; set; } = new();

    // Legacy aliases retained so older completed packs remain importable.
    public string? Edition { get; set; }
    public List<string> Guests { get; set; } = new();

    public string? ArchiveNotes { get; set; }
    public List<TrvPackMoment> Moments { get; set; } = new();
}

public sealed class TrvPackBroadcastMetadata
{
    public string? Station { get; set; }
    public string? Slot { get; set; }
    public string? Variant { get; set; }
    public string? Era { get; set; }
    public string? EpisodeType { get; set; }
}

public sealed class TrvPackCatalogueMetadata
{
    public string? Series { get; set; }
    public string? Programme { get; set; }
    public string? Format { get; set; }
    public string? OriginalReleaseDate { get; set; }
    public string? RecordingDate { get; set; }
    public string? Venue { get; set; }
    public string? Event { get; set; }
    public string? Network { get; set; }
    public string? CatalogueNumber { get; set; }
    public string? OriginalFilename { get; set; }
    public string? Provenance { get; set; }
    public string? ResearchNotes { get; set; }
    public string? DateReviewStatus { get; set; }
    public string? DateReviewDate { get; set; }
    public string? DateReviewBasis { get; set; }
    public string? DateReviewNotes { get; set; }
    public string? DateReviewedAt { get; set; }
    public string? DateReviewPreviousAirDate { get; set; }
    public string? DateReviewPreviousConfidence { get; set; }
}

public sealed class TrvPackPeople
{
    public List<string> Hosts { get; set; } = new();
    public List<string> Guests { get; set; } = new();
    public List<string> Callers { get; set; } = new();
    public List<string> MentionedPeople { get; set; } = new();
}

public sealed class TrvPackResearchQuality
{
    public int Confidence { get; set; }
    public string? ConfidenceReason { get; set; }
}

public sealed class TrvPackMoment
{
    public long TimestampSeconds { get; set; }
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public List<string> Tags { get; set; } = new();
    public string TimestampDisplay => TimeSpan.FromSeconds(Math.Max(0, TimestampSeconds)).ToString(@"hh\:mm\:ss");
    public string TagsDisplay => Tags is { Count: > 0 } ? string.Join(" · ", Tags) : "";
}

public sealed class TrvPackSource
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public string Publisher { get; set; } = "";
    public string Accessed { get; set; } = "";
    public List<string> Supports { get; set; } = new();
    public string? Notes { get; set; }
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title)
        ? Title
        : !string.IsNullOrWhiteSpace(Publisher) ? Publisher : Url;
    public string SupportsDisplay => Supports is { Count: > 0 }
        ? $"Supports: {string.Join(", ", Supports)}"
        : "";
    public string AccessedDisplay => string.IsNullOrWhiteSpace(Accessed) ? "" : $"Accessed {Accessed}";
    public string NotesDisplay => Notes?.Trim() ?? "";
}

public sealed class TrvPackImportPolicy
{
    /// <summary>
    /// Treats the research-owned fields in this record as an audited canonical snapshot.
    /// Empty values intentionally clear stale metadata, manual-edit protection is bypassed
    /// for those fields, and durable Research Library children are replaced rather than merged.
    /// Identity, media, playback and personal-state fields are never changed by this mode.
    /// </summary>
    public bool AuthoritativeAudit { get; set; }
    public bool ReplaceExistingHeadline { get; set; }
    public bool ReplaceExistingSummary { get; set; }
    public bool MergeGuests { get; set; } = true;
    public bool MergePeople { get; set; } = true;
    public bool MergeTopics { get; set; } = true;
    public bool MergeMoments { get; set; } = true;
}

public sealed class TrvKnowledgePack
{
    public TrvPackManifest Manifest { get; set; } = new();
    public List<TrvPackBroadcast> Broadcasts { get; set; } = new();
    public List<TrvPackBroadcast> MissingBroadcasts { get; set; } = new();
    public List<TrvPackTranscript> Transcripts { get; set; } = new();
    public TheRadioVault.Services.Models.WikiAuthoringSnapshot? Wiki { get; set; }
}

/// <summary>Portable, read-only transcript context carried by a Deep Research Pack.</summary>
public sealed class TrvPackTranscript
{
    public string BroadcastId { get; set; } = "";
    public string Show { get; set; } = "";
    public string? BroadcastDate { get; set; }
    public int PartNumber { get; set; } = 1;
    public string Status { get; set; } = "Complete";
    public string Language { get; set; } = "";
    public string Engine { get; set; } = "";
    public string Model { get; set; } = "";
    public string FullText { get; set; } = "";
    public bool HasSpeakerDiarization { get; set; }
    public List<TrvPackTranscriptSegment> Segments { get; set; } = new();
}

public sealed class TrvPackTranscriptSegment
{
    public int Index { get; set; }
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public string Speaker { get; set; } = "";
    public string SpeakerKey { get; set; } = "";
    public string Text { get; set; } = "";
}

public sealed class KnowledgePackImportResult
{
    public int Total { get; set; }
    public int Matched { get; set; }
    public int Updated { get; set; }
    public int RetainedMissing { get; set; }
    public int Ambiguous { get; set; }
    public int ResolvedPreviousMissing { get; set; }
    public int ResearchRecordsStored { get; set; }
    public int AttachedResearchRecords { get; set; }
    public int ConfirmedMissing { get; set; }
    public int ProbableMissing { get; set; }
    public int UnknownGaps { get; set; }
    public int ConflictsCreated { get; set; }
    public long ImportRunId { get; set; }
    public int FieldsApplied { get; set; }
    public int FieldsMerged { get; set; }
    public int FieldsPreserved { get; set; }
    public int ManualFieldsProtected { get; set; }
    public int ChangeRecordsWritten { get; set; }
}

public sealed class MissingBroadcastResearchRecord
{
    public long Id { get; set; }
    public string BroadcastId { get; set; } = "";
    public string Show { get; set; } = "";
    public DateTime? BroadcastDate { get; set; }
    public string Slot { get; set; } = "";
    public int PartNumber { get; set; } = 1;
    public int? TotalParts { get; set; }
    public string Headline { get; set; } = "";
    public string Summary { get; set; } = "";
    public int Confidence { get; set; }
    public string ConfidenceReason { get; set; } = "";
    public string Status { get; set; } = "pending";
    public long? MatchedEpisodeId { get; set; }
    public string MatchNotes { get; set; } = "";
    public DateTime UpdatedAt { get; set; }

    public string BroadcastDateDisplay => BroadcastDate?.ToString("dd MMM yyyy") ?? "Date unknown";
    public string PartDisplay => TotalParts is > 1
        ? $"Part {PartNumber} of {TotalParts}"
        : PartNumber > 1 ? $"Part {PartNumber}" : "";
    public string IdentityDisplay
    {
        get
        {
            var bits = new List<string> { BroadcastDateDisplay };
            if (!string.IsNullOrWhiteSpace(Slot)) bits.Add(Slot);
            if (!string.IsNullOrWhiteSpace(PartDisplay)) bits.Add(PartDisplay);
            return string.Join(" · ", bits);
        }
    }
    public string HeadlineDisplay => string.IsNullOrWhiteSpace(Headline) ? "Untitled researched broadcast" : Headline;
    public string SummaryDisplay => string.IsNullOrWhiteSpace(Summary) ? "No summary has been added yet." : Summary;
    public string StatusDisplay => Status switch
    {
        "pending" => "Broadcast lead",
        "ambiguous" => "Needs your decision",
        "resolved" => "Matched",
        "ignored" => "Ignored",
        _ => Status
    };
    public string ConfidenceDisplay => Confidence > 0 ? $"{Confidence}% confidence" : "Confidence not rated";
    public string UpdatedDisplay => $"Updated {UpdatedAt.ToLocalTime():dd MMM yyyy HH:mm}";
}

public sealed class MissingBroadcastResearchDetails
{
    public MissingBroadcastResearchRecord Record { get; set; } = new();
    public TrvPackBroadcast Broadcast { get; set; } = new();
    public IReadOnlyList<string> Hosts { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Guests { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Callers { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> MentionedPeople { get; set; } = Array.Empty<string>();
    public IReadOnlyList<string> Topics { get; set; } = Array.Empty<string>();
    public IReadOnlyList<TrvPackSource> Sources { get; set; } = Array.Empty<TrvPackSource>();
    public IReadOnlyList<TrvPackMoment> Moments { get; set; } = Array.Empty<TrvPackMoment>();
}

public sealed class MissingResearchSummary
{
    public int Pending { get; set; }
    public int Ambiguous { get; set; }
    public int Resolved { get; set; }
    public int Ignored { get; set; }
}

public sealed class MissingResearchReconciliationResult
{
    public int Applied { get; set; }
    public int Ambiguous { get; set; }
    public int Invalid { get; set; }
    public int CandidatesFound { get; set; }
    public int PreviouslyMissingMatches { get; set; }
    public int AlternateCaptureCandidates { get; set; }
}


public enum GlobalSearchResultKind { Episode, Moment, Research }

public sealed class GlobalSearchResult
{
    public GlobalSearchResultKind Kind { get; set; }
    public long EpisodeId { get; set; }
    public long? MomentId { get; set; }
    public long? ResearchBroadcastId { get; set; }
    public long PositionMs { get; set; }
    public string CollectionName { get; set; } = "";
    public DateTime? AirDate { get; set; }
    public string AirDateDisplay => AirDate?.ToString("dd MMM yyyy") ?? "Unknown";
    public string Headline { get; set; } = "";
    public string MatchTitle { get; set; } = "";
    public string MatchExcerpt { get; set; } = "";
    public string Guests { get; set; } = "";
    public string Topics { get; set; } = "";
    public string Status { get; set; } = "Unplayed";
    public bool Favourite { get; set; }
    public string KindDisplay => Kind switch
    {
        GlobalSearchResultKind.Moment => "Moment",
        GlobalSearchResultKind.Research => "Research",
        _ => "Episode"
    };
    public string ResultTitle => string.IsNullOrWhiteSpace(MatchTitle) ? CollectionName : MatchTitle;
    public string ResultSubtitle => Kind switch
    {
        GlobalSearchResultKind.Moment => $"{CollectionName} · {AirDateDisplay} · {TimeSpan.FromMilliseconds(PositionMs).ToString(@"hh\:mm\:ss")}",
        GlobalSearchResultKind.Research => $"{CollectionName} · {AirDateDisplay} · {Status}",
        _ => $"{CollectionName} · {AirDateDisplay} · {Status}"
    };
}
