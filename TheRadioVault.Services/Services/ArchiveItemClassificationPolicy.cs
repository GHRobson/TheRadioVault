using System.Text.RegularExpressions;
using TheRadioVault.Core.LibraryTruth;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Identifies established clip and compilation naming conventions without using
/// duration or inventing an air date. Deliberately conservative: every file in
/// an undated identity group must match the same known archive convention.
/// </summary>
internal static partial class ArchiveItemClassificationPolicy
{
    internal sealed record Result(bool IsArchiveItem, string Kind, string Evidence)
    {
        public static Result Broadcast { get; } = new(false, "Broadcast", string.Empty);
    }

    public static Result Classify(IReadOnlyList<LibraryTruthInterpretation> files)
    {
        if (files.Count == 0 || files.Any(file => file.AirDate.HasValue)) return Result.Broadcast;

        var matches = files.Select(ClassifyFile).ToArray();
        if (matches.Any(match => !match.IsArchiveItem)) return Result.Broadcast;
        var kinds = matches.Select(match => match.Kind).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (kinds.Length != 1) return Result.Broadcast;

        return new Result(
            true,
            kinds[0],
            string.Join(" ", matches.Select(match => match.Evidence).Distinct(StringComparer.OrdinalIgnoreCase)));
    }

    private static Result ClassifyFile(LibraryTruthInterpretation file)
    {
        var path = NormalizePath(file.Input.Path);
        var filename = file.Input.FilenameWithoutExtension;

        if (IsCollection(file, "Opie & Anthony") && path.Contains("/oadementedworldcd/", StringComparison.Ordinal))
            return new(true, "Compilation track", "The file is stored in the established O&A Demented World CD compilation folder.");

        if (IsCollection(file, "Opie & Anthony") && path.Contains("/earlychipchipperson/", StringComparison.Ordinal))
            return new(true, "Compilation track", "The file is stored in the established Early Chip Chipperson compilation folder.");

        if (IsCollection(file, "Opie & Anthony") &&
            path.Contains("/afro shows/", StringComparison.Ordinal) &&
            filename.Contains(" - AFRO - ", StringComparison.OrdinalIgnoreCase))
            return new(true, "Topical clip", "The parent folder and filename use the established cross-show AFRO topical-clip convention.");

        if (IsCollection(file, "The Ron & Ron Show") && RonAndRonTopicalClip().IsMatch(filename))
            return new(true, "Topical clip", "The filename uses the established year-only Ron & Ron topical-clip convention.");

        return Result.Broadcast;
    }

    private static bool IsCollection(LibraryTruthInterpretation file, string expected)
        => file.CollectionName.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
           file.Input.AssignedCollectionName.Equals(expected, StringComparison.OrdinalIgnoreCase) ||
           file.Input.CurrentCollectionName.Equals(expected, StringComparison.OrdinalIgnoreCase);

    private static string NormalizePath(string value)
        => "/" + (value ?? string.Empty).Replace('\\', '/').Trim('/').ToLowerInvariant() + "/";

    [GeneratedRegex(@"^\s*(?:19|20)\d{2}[\s._-]+(?:the[\s._-]+)?ron[\s._-]*(?:&|and)[\s._-]*ron\b.+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RonAndRonTopicalClip();
}
