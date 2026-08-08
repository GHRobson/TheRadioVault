using System.Text.Json.Serialization;

namespace TheRadioVault.Models;

public sealed record PreservationScanOptions(
    bool ScanMissingEvidence = true,
    bool RetryPreviousErrors = true,
    bool ReinspectAllLocalFiles = false,
    bool HashStrongDuplicateCandidates = true)
{
    public string ModeDisplay => ReinspectAllLocalFiles
        ? "Reinspect every locally available recording"
        : "Fill missing preservation evidence";
}

public sealed class PreservationFileCandidate
{
    public long MediaFileId { get; init; }
    public long EpisodeId { get; init; }
    public string Path { get; init; } = "";
    public string Filename { get; init; } = "";
    public long FileSize { get; init; }
    public long DurationMs { get; init; }
    public string PartialHash { get; init; } = "";
    public string FullHash { get; init; } = "";
    public string InspectionError { get; init; } = "";
    public bool IsPreferred { get; init; }
}

public sealed record PreservationScanProgress(
    int Completed,
    int Total,
    string CurrentFile,
    string Message,
    int Fingerprinted,
    int FullHashed,
    int Errors,
    double Percent);

public sealed class PreservationScanResult
{
    public int FilesConsidered { get; set; }
    public int FilesInspected { get; set; }
    public int Fingerprinted { get; set; }
    public int FullHashed { get; set; }
    public int Errors { get; set; }
    public bool Cancelled { get; set; }
    public long RunId { get; set; }

    public string Summary => Cancelled
        ? $"Preservation scan cancelled after {FilesInspected:N0} file(s). Completed evidence is retained."
        : $"Inspected {FilesInspected:N0} file(s), created {Fingerprinted:N0} partial fingerprint(s), {FullHashed:N0} full hash(es), with {Errors:N0} error(s).";
}

public sealed class PreservationSummary
{
    public int TotalFiles { get; init; }
    public int LocalFiles { get; init; }
    public int MissingEvidence { get; init; }
    public int PartialFingerprints { get; init; }
    public int FullHashes { get; init; }
    public int StrongDuplicateFilesAwaitingFullHash { get; init; }
    public int InspectionErrors { get; init; }
    public DateTimeOffset? LastCompletedScanAt { get; init; }
    public string LastCompletedScanDisplay => LastCompletedScanAt?.LocalDateTime.ToString("g") ?? "Never";
}

public sealed class ArchiveManifest
{
    public const int CurrentSchemaVersion = 1;
    public string Format { get; set; } = "RadioVaultArchiveManifest";
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public string AppVersion { get; set; } = "";
    public DateTimeOffset GeneratedAt { get; set; }
    public ArchiveManifestMachine Machine { get; set; } = new();
    public List<ArchiveManifestRoot> LibraryRoots { get; set; } = new();
    public List<ArchiveManifestFile> Files { get; set; } = new();
}

public sealed class ArchiveManifestMachine
{
    public string MachineId { get; set; } = "";
    public string MachineName { get; set; } = "";
    public string OperatingSystem { get; set; } = "";
}

public sealed class ArchiveManifestRoot
{
    public int RootId { get; set; }
    public string DisplayName { get; set; } = "";
    public string Path { get; set; } = "";
    public string AssignedCollection { get; set; } = "";
}

public sealed class ArchiveManifestFile
{
    public string FileKey { get; set; } = "";
    public string BroadcastUid { get; set; } = "";
    public string Show { get; set; } = "";
    public string? AirDate { get; set; }
    public string BroadcastSlot { get; set; } = "";
    public int PartNumber { get; set; } = 1;
    public int? TotalParts { get; set; }
    public string Headline { get; set; } = "";
    public int RootId { get; set; }
    public string RelativePath { get; set; } = "";
    public string Filename { get; set; } = "";
    public string Extension { get; set; } = "";
    public long FileSize { get; set; }
    public long DurationMs { get; set; }
    public string PartialSha256 { get; set; } = "";
    public string FullSha256 { get; set; } = "";
    public string StorageState { get; set; } = "";
    public bool IsPreferred { get; set; }
    public DateTimeOffset? ModifiedAt { get; set; }

    [JsonIgnore]
    public string IdentityKey => ArchiveComparisonIdentity.Create(Show, AirDate, BroadcastSlot, PartNumber);
    [JsonIgnore]
    public string DurationDisplay => DurationMs <= 0 ? "Unknown" : TimeSpan.FromMilliseconds(DurationMs).ToString(DurationMs >= 3_600_000 ? @"h\:mm\:ss" : @"m\:ss");
    [JsonIgnore]
    public string SizeDisplay => FileSize <= 0 ? "Unknown" : FormatBytes(FileSize);

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var amount = (double)value;
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1) { amount /= 1024; unit++; }
        return $"{amount:0.##} {units[unit]}";
    }
}

public enum ArchiveComparisonKind
{
    ExactCopy,
    ExactAudioConflictingIdentity,
    StrongFingerprintCandidate,
    FingerprintConflictingIdentity,
    AlternateEncode,
    PartialOrDifferentCoverage,
    SameBroadcastUnverified,
    UniqueToOtherMachine,
    UniqueToThisMachine
}

public sealed class ArchiveComparisonItem
{
    public ArchiveComparisonKind Kind { get; init; }
    public string Confidence { get; init; } = "";
    public string Explanation { get; init; } = "";
    public string Recommendation { get; init; } = "";
    public ArchiveManifestFile? Local { get; init; }
    public ArchiveManifestFile? Other { get; init; }

    public string Classification => Kind switch
    {
        ArchiveComparisonKind.ExactCopy => "Confirmed exact copy",
        ArchiveComparisonKind.ExactAudioConflictingIdentity => "Exact bytes, conflicting identity",
        ArchiveComparisonKind.StrongFingerprintCandidate => "Strong copy candidate",
        ArchiveComparisonKind.FingerprintConflictingIdentity => "Matching fingerprint, conflicting identity",
        ArchiveComparisonKind.AlternateEncode => "Same broadcast, alternate encode",
        ArchiveComparisonKind.PartialOrDifferentCoverage => "Possible partial or different coverage",
        ArchiveComparisonKind.SameBroadcastUnverified => "Same broadcast, not yet verified",
        ArchiveComparisonKind.UniqueToOtherMachine => "Only on other computer",
        ArchiveComparisonKind.UniqueToThisMachine => "Only on this computer",
        _ => Kind.ToString()
    };

    public string Show => Other?.Show ?? Local?.Show ?? "";
    public string Broadcast => BuildBroadcast(Other ?? Local);
    public string LocalFile => Local?.Filename ?? "—";
    public string OtherFile => Other?.Filename ?? "—";
    public string LocalDetails => Local is null ? "—" : $"{Local.DurationDisplay} · {Local.SizeDisplay}";
    public string OtherDetails => Other is null ? "—" : $"{Other.DurationDisplay} · {Other.SizeDisplay}";

    private static string BuildBroadcast(ArchiveManifestFile? file)
    {
        if (file is null) return "";
        var date = DateTime.TryParse(file.AirDate, out var parsed) ? parsed.ToString("d MMM yyyy") : "Unknown date";
        var slot = string.IsNullOrWhiteSpace(file.BroadcastSlot) ? "" : $" · {file.BroadcastSlot}";
        var part = file.PartNumber > 1 || file.TotalParts.GetValueOrDefault() > 1 ? $" · Part {file.PartNumber}" : "";
        return $"{date}{slot}{part}";
    }
}

public sealed class ArchiveComparisonReport
{
    public string LocalMachineName { get; init; } = "";
    public string OtherMachineName { get; init; } = "";
    public DateTimeOffset ComparedAt { get; init; }
    public IReadOnlyList<ArchiveComparisonItem> Items { get; init; } = Array.Empty<ArchiveComparisonItem>();

    public int ExactCopies => Items.Count(x => x.Kind == ArchiveComparisonKind.ExactCopy);
    public int ConflictingIdentity => Items.Count(x => x.Kind is ArchiveComparisonKind.ExactAudioConflictingIdentity or ArchiveComparisonKind.FingerprintConflictingIdentity);
    public int AlternateEncodes => Items.Count(x => x.Kind == ArchiveComparisonKind.AlternateEncode);
    public int PartialOrCoverage => Items.Count(x => x.Kind == ArchiveComparisonKind.PartialOrDifferentCoverage);
    public int UniqueToLocal => Items.Count(x => x.Kind == ArchiveComparisonKind.UniqueToThisMachine);
    public int UniqueToOther => Items.Count(x => x.Kind == ArchiveComparisonKind.UniqueToOtherMachine);
    public int NeedsMoreEvidence => Items.Count(x => x.Kind is ArchiveComparisonKind.StrongFingerprintCandidate or ArchiveComparisonKind.SameBroadcastUnverified);
}

public static class ArchiveComparisonIdentity
{
    public static string Create(string? show, string? airDate, string? slot, int partNumber)
        => string.Join("|", Normalize(show), NormalizeDate(airDate), NormalizeSlot(slot), Math.Max(1, partNumber));

    private static string Normalize(string? value)
        => string.Join(' ', (value ?? "").Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static string NormalizeDate(string? value)
        => DateTime.TryParse(value, out var parsed) ? parsed.ToString("yyyy-MM-dd") : (value ?? "").Trim();

    private static string NormalizeSlot(string? value)
    {
        var slot = Normalize(value);
        if (slot is "pm" or "afternoon" or "afternoon show" or "evening" or "evening show") return "pm";
        if (slot is "am" or "morning" or "morning show") return "am";
        if (slot.Contains("opieradio", StringComparison.OrdinalIgnoreCase)) return "opieradio";
        return slot;
    }
}
