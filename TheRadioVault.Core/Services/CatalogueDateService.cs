using System.Globalization;
using System.Text.RegularExpressions;

namespace TheRadioVault.Core.Services;

public enum CatalogueDatePrecision
{
    None,
    Year,
    Month,
    Day
}

public sealed record CatalogueDateHint(
    DateOnly? ExactDate,
    string DisplayText,
    CatalogueDatePrecision Precision)
{
    public bool HasValue => Precision != CatalogueDatePrecision.None && !string.IsNullOrWhiteSpace(DisplayText);
    public static CatalogueDateHint None { get; } = new(null, string.Empty, CatalogueDatePrecision.None);
}

/// <summary>
/// Reads trustworthy exact dates and non-invented partial date clues from
/// catalogue-style filenames and research fields. Month/year and year-only
/// clues remain partial metadata; they are never converted to an arbitrary day.
/// </summary>
public static class CatalogueDateService
{
    private static readonly string[] ExactFormats =
    {
        "yyyy-MM-dd", "yyyy/MM/dd", "yyyy.MM.dd", "yyyy_MM_dd",
        "M/d/yyyy", "MM/dd/yyyy", "M-d-yyyy", "MM-dd-yyyy",
        "d/M/yyyy", "dd/MM/yyyy", "d-M-yyyy", "dd-MM-yyyy",
        "MMMM d yyyy", "MMMM dd yyyy", "MMM d yyyy", "MMM dd yyyy",
        "d MMMM yyyy", "dd MMMM yyyy", "d MMM yyyy", "dd MMM yyyy"
    };

    private static readonly Regex IsoDateRegex = new(
        @"(?<!\d)(?<year>19\d{2}|20\d{2})[-_./](?<month>0?[1-9]|1[0-2])[-_./](?<day>0?[1-9]|[12]\d|3[01])(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex NamedDateRegex = new(
        @"(?ix)(?<![A-Za-z])(?:
            (?<month>January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)
            [\s._-]+(?<day>\d{1,2})(?:st|nd|rd|th)?[,]?[\s._-]+(?<year>19\d{2}|20\d{2})
          |
            (?<day2>\d{1,2})(?:st|nd|rd|th)?[\s._-]+(?<month2>January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)
            [,]?[\s._-]+(?<year2>19\d{2}|20\d{2})
        )(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex MonthYearRegex = new(
        @"(?ix)(?<![A-Za-z])(?<month>January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)[\s._,-]+(?<year>19\d{2}|20\d{2})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace);

    private static readonly Regex YearRegex = new(
        @"(?<!\d)(?<year>19\d{2}|20\d{2})(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static CatalogueDateHint Resolve(params string?[] candidates)
    {
        foreach (var candidate in Clean(candidates))
        {
            var exact = TryExact(candidate);
            if (exact.HasValue)
                return new CatalogueDateHint(exact, exact.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), CatalogueDatePrecision.Day);
        }

        foreach (var candidate in Clean(candidates))
        {
            var match = MonthYearRegex.Match(candidate);
            if (!match.Success) continue;
            var monthText = match.Groups["month"].Value.Equals("Sept", StringComparison.OrdinalIgnoreCase)
                ? "Sep"
                : match.Groups["month"].Value;
            if (!DateTime.TryParseExact(monthText, new[] { "MMMM", "MMM" }, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var month)) continue;
            if (!int.TryParse(match.Groups["year"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var year)) continue;
            return new CatalogueDateHint(null, $"{CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(month.Month)} {year}", CatalogueDatePrecision.Month);
        }

        foreach (var candidate in Clean(candidates))
        {
            var match = YearRegex.Match(candidate);
            if (match.Success)
                return new CatalogueDateHint(null, match.Groups["year"].Value, CatalogueDatePrecision.Year);
        }

        return CatalogueDateHint.None;
    }

    public static DateOnly? ResolveExactDate(params string?[] candidates) => Resolve(candidates).ExactDate;

    public static string ResolveDisplayText(params string?[] candidates) => Resolve(candidates).DisplayText;

    public static string FormatForDisplay(string? value)
    {
        var hint = Resolve(value);
        if (!hint.HasValue) return value?.Trim() ?? string.Empty;
        return hint.ExactDate?.ToString("d MMMM yyyy", CultureInfo.CurrentCulture) ?? hint.DisplayText;
    }

    private static DateOnly? TryExact(string candidate)
    {
        var trimmed = candidate.Trim().Trim('(', ')', '[', ']', '{', '}');
        trimmed = Regex.Replace(trimmed, @"(?<=\d)(st|nd|rd|th)\b", string.Empty, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        trimmed = Regex.Replace(trimmed, @"[,._]+", " ");
        trimmed = Regex.Replace(trimmed, @"\bSept\b", "Sep", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        trimmed = Regex.Replace(trimmed, @"\s+", " ").Trim();
        foreach (var format in ExactFormats)
        {
            if (DateOnly.TryParseExact(trimmed, format, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var exact)
                && exact.Year is >= 1920 and <= 2100)
                return exact;
        }

        var iso = IsoDateRegex.Match(candidate);
        if (iso.Success
            && int.TryParse(iso.Groups["year"].Value, out var isoYear)
            && int.TryParse(iso.Groups["month"].Value, out var isoMonth)
            && int.TryParse(iso.Groups["day"].Value, out var isoDay))
        {
            try { return new DateOnly(isoYear, isoMonth, isoDay); }
            catch (ArgumentOutOfRangeException) { }
        }

        var named = NamedDateRegex.Match(candidate);
        if (!named.Success) return null;
        var monthText = named.Groups["month"].Success ? named.Groups["month"].Value : named.Groups["month2"].Value;
        if (monthText.Equals("Sept", StringComparison.OrdinalIgnoreCase)) monthText = "Sep";
        var dayText = named.Groups["day"].Success ? named.Groups["day"].Value : named.Groups["day2"].Value;
        var yearText = named.Groups["year"].Success ? named.Groups["year"].Value : named.Groups["year2"].Value;
        if (!DateTime.TryParseExact(monthText, new[] { "MMMM", "MMM" }, CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var monthValue)
            || !int.TryParse(dayText, out var day)
            || !int.TryParse(yearText, out var year)) return null;
        try { return new DateOnly(year, monthValue.Month, day); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static IEnumerable<string> Clean(IEnumerable<string?> values) => values
        .Where(value => !string.IsNullOrWhiteSpace(value))
        .Select(value => value!.Trim());
}
