using TheRadioVault.Core.Services;

namespace TheRadioVault.Core.LibraryTruth;

public enum LibraryTruthConfidence
{
    Unknown,
    Low,
    Probable,
    High
}

public sealed record LibraryTruthEvidence(
    string Field,
    string Value,
    int Weight,
    string Source,
    string Reasoning);

public sealed record LibraryTruthWarning(
    string Code,
    string Message,
    bool NeedsReview = true);

public sealed class LibraryTruthFileInput
{
    public long MediaFileId { get; init; }
    public long CurrentEpisodeId { get; init; }
    public string Path { get; init; } = string.Empty;
    public string OriginalFilename { get; init; } = string.Empty;
    public long FileSize { get; init; }
    public long DurationMs { get; init; }
    public string PartialHash { get; init; } = string.Empty;
    public string FullHash { get; init; } = string.Empty;
    public string StorageState { get; init; } = string.Empty;
    public bool IsPreferred { get; init; }
    public string CurrentCollectionName { get; init; } = string.Empty;
    public DateOnly? CurrentAirDate { get; init; }
    public string CurrentDateConfidence { get; init; } = "Unknown";
    public string CurrentBroadcastSlot { get; init; } = string.Empty;
    public int CurrentPartNumber { get; init; } = 1;
    public int? CurrentTotalParts { get; init; }
    public string CurrentTitle { get; init; } = string.Empty;
    public string CurrentBroadcastUid { get; init; } = string.Empty;
    public string LibraryRoot { get; init; } = string.Empty;
    public string AssignedCollectionName { get; init; } = string.Empty;

    public string FilenameWithoutExtension => ArchivePath.GetFileNameWithoutExtension(
        string.IsNullOrWhiteSpace(OriginalFilename) ? Path : OriginalFilename);

    public string DirectoryPath => ArchivePath.GetDirectoryName(Path) ?? LibraryRoot;
}

public sealed class LibraryTruthFolderContext
{
    public string ContextKey { get; init; } = string.Empty;
    public string LibraryRoot { get; init; } = string.Empty;
    public string AssignedCollectionName { get; init; } = string.Empty;
    public string DominantCollectionName { get; init; } = string.Empty;
    public int? YearHint { get; init; }
    public string DateOrder { get; init; } = "Unknown";
    public int FileCount { get; init; }
    public IReadOnlyList<LibraryTruthEvidence> Evidence { get; init; } = Array.Empty<LibraryTruthEvidence>();
}

public sealed class LibraryTruthInterpretation
{
    public required LibraryTruthFileInput Input { get; init; }
    public string ParserVersion { get; init; } = string.Empty;
    public string CollectionName { get; init; } = "Unsorted";
    public DateOnly? AirDate { get; init; }
    public string DateConfidence { get; init; } = "Unknown";
    public string BroadcastSlot { get; init; } = string.Empty;
    public string CanonicalSlot { get; init; } = string.Empty;
    public int PartNumber { get; init; } = 1;
    public int? TotalParts { get; init; }
    public string MultipartKind { get; init; } = string.Empty;
    public string Headline { get; init; } = string.Empty;
    public int ConfidenceScore { get; init; }
    public LibraryTruthConfidence Confidence { get; init; }
    public string CanonicalBroadcastKey { get; init; } = string.Empty;
    public string CurrentIdentityKey { get; init; } = string.Empty;
    public string Disposition { get; init; } = string.Empty;
    public string ChangeSummary { get; init; } = string.Empty;
    public IReadOnlyList<LibraryTruthEvidence> Evidence { get; init; } = Array.Empty<LibraryTruthEvidence>();
    public IReadOnlyList<LibraryTruthWarning> Warnings { get; init; } = Array.Empty<LibraryTruthWarning>();

    public bool NeedsReview => Warnings.Any(x => x.NeedsReview) || Confidence is LibraryTruthConfidence.Unknown or LibraryTruthConfidence.Low;
    public bool HasMeaningfulChange => !string.Equals(Disposition, "Unchanged", StringComparison.OrdinalIgnoreCase);
}

public static class LibraryTruthIdentity
{
    public static string Build(string? collection, DateOnly? airDate, string? canonicalSlot, long unknownSeed, string? fullHash = null)
    {
        var show = Normalize(collection, "UNSORTED");
        if (!airDate.HasValue)
        {
            var exactIdentity = string.IsNullOrWhiteSpace(fullHash)
                ? $"FILE-{unknownSeed}"
                : "FULL-" + Normalize(fullHash, $"FILE-{unknownSeed}");
            return $"{show}|UNKNOWN|{exactIdentity}";
        }
        var slot = Normalize(canonicalSlot, "STANDARD");
        return $"{show}|{airDate.Value:yyyy-MM-dd}|{slot}";
    }

    public static string BuildCurrent(string? collection, DateOnly? airDate, string? slot, int partNumber, long episodeId)
    {
        var show = Normalize(collection, "UNSORTED");
        if (!airDate.HasValue) return $"{show}|UNKNOWN|EPISODE-{episodeId}";
        var slotToken = Normalize(TheRadioVault.Core.Services.BroadcastSlotNormalizer.Canonicalize(slot), "STANDARD");
        return $"{show}|{airDate.Value:yyyy-MM-dd}|{slotToken}|P{Math.Max(1, partNumber)}";
    }

    public static string Normalize(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        var buffer = new System.Text.StringBuilder(value.Length);
        var pendingDash = false;
        foreach (var character in value.Trim().ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingDash && buffer.Length > 0) buffer.Append('-');
                buffer.Append(character);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }
        return buffer.Length == 0 ? fallback : buffer.ToString();
    }
}
