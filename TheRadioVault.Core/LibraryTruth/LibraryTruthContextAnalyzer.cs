using System.Text.RegularExpressions;
using TheRadioVault.Core.Services;

namespace TheRadioVault.Core.LibraryTruth;

public sealed class LibraryTruthContextAnalyzer
{
    private static readonly Regex YearComponent = new(@"(?<!\d)(?<year>(?:19|20)\d{2})(?!\d)", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex YearLast = new(@"(?<!\d)(?<first>\d{1,2})[\s._/-]+(?<second>\d{1,2})[\s._/-]+(?:19|20)\d{2}(?!\d)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public IReadOnlyDictionary<string, LibraryTruthFolderContext> Analyse(IEnumerable<LibraryTruthFileInput> files)
    {
        var items = files.ToArray();
        var result = new Dictionary<string, LibraryTruthFolderContext>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in items.GroupBy(ContextKey, StringComparer.OrdinalIgnoreCase))
        {
            var sample = group.ToArray();
            var explicitShows = sample
                .Select(item => LibraryTruthParser.DetectExplicitCollection(item.FilenameWithoutExtension))
                .Where(show => !string.IsNullOrWhiteSpace(show))
                .GroupBy(show => show!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(show => show.Count())
                .ToArray();
            var dominant = explicitShows.FirstOrDefault();
            var assigned = sample.Select(x => x.AssignedCollectionName)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && !x.Equals("Auto detect", StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            var yearHint = FindYearHint(group.Key, sample[0].LibraryRoot);
            var dateOrder = InferDateOrder(sample, dominant?.Key ?? assigned);
            var evidence = new List<LibraryTruthEvidence>();
            if (dominant is not null && dominant.Count() >= Math.Max(2, (int)Math.Ceiling(sample.Length * 0.60)))
                evidence.Add(new LibraryTruthEvidence("show-context", dominant.Key, 68, "neighbouring files",
                    $"{dominant.Count()} of {sample.Length} files in this folder explicitly name {dominant.Key}."));
            if (yearHint.HasValue)
                evidence.Add(new LibraryTruthEvidence("year-context", yearHint.Value.ToString(), 72, "folder path",
                    "A four-digit year folder can complete month-day-only filenames without guessing."));
            if (dateOrder != "Unknown")
                evidence.Add(new LibraryTruthEvidence("date-order", dateOrder, 70, "folder context",
                    dateOrder == "US" ? "Neighbouring filenames or the recognised US radio show establish month-day-year order." : "Neighbouring unambiguous filenames establish day-month-year order."));

            result[group.Key] = new LibraryTruthFolderContext
            {
                ContextKey = group.Key,
                LibraryRoot = sample[0].LibraryRoot,
                AssignedCollectionName = assigned,
                DominantCollectionName = dominant is not null && dominant.Count() >= Math.Max(2, (int)Math.Ceiling(sample.Length * 0.60))
                    ? dominant.Key
                    : string.Empty,
                YearHint = yearHint,
                DateOrder = dateOrder,
                FileCount = sample.Length,
                Evidence = evidence
            };
        }
        return result;
    }

    public static string ContextKey(LibraryTruthFileInput input)
        => string.IsNullOrWhiteSpace(input.DirectoryPath) ? input.LibraryRoot : input.DirectoryPath;


    private static string InferDateOrder(IReadOnlyList<LibraryTruthFileInput> sample, string? collection)
    {
        var mdy = 0;
        var dmy = 0;
        foreach (var item in sample)
        {
            foreach (Match match in YearLast.Matches(item.FilenameWithoutExtension))
            {
                if (!int.TryParse(match.Groups["first"].Value, out var first) ||
                    !int.TryParse(match.Groups["second"].Value, out var second)) continue;
                if (first <= 12 && second > 12) mdy++;
                else if (first > 12 && second <= 12) dmy++;
            }
        }
        if (mdy >= 2 && mdy >= dmy * 2) return "US";
        if (dmy >= 2 && dmy >= mdy * 2) return "DMY";
        return collection?.Equals("AFRO Show", StringComparison.OrdinalIgnoreCase) == true
            || KnownShowCatalog.UsesUsArchiveDateOrder(collection)
            ? "US"
            : "Unknown";
    }

    private static int? FindYearHint(string path, string root)
    {
        var relative = path;
        if (!string.IsNullOrWhiteSpace(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            relative = path[root.Length..];
        foreach (var component in ArchivePath.Components(relative).Reverse())
        {
            var matches = YearComponent.Matches(component);
            for (var index = matches.Count - 1; index >= 0; index--)
                if (int.TryParse(matches[index].Groups["year"].Value, out var year) && year is >= 1980 and <= 2035)
                    return year;
        }
        return null;
    }
}
