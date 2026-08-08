using System.Text.RegularExpressions;

namespace TheRadioVault.Core.Services;

/// <summary>Extracts multipart recording information without changing the source filename.</summary>
public static class MultipartParser
{
    private static readonly Regex LabelledOfPattern = new(
        @"(?:^|[\s_\-.()\[\]])(?<kind>part|pt|segment|seg|disc|disk|cd|hour|hr)[\s_\-.]*(?<part>\d{1,2})[\s_\-.]*(?:of|/)[\s_\-.]*(?<total>\d{1,2})(?:$|[\s_\-.()\[\]])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BareOfPattern = new(
        @"(?:^|[\s_\-.()\[\]])(?<part>\d{1,2})[\s_\-.]*(?:of|/)[\s_\-.]*(?<total>\d{1,2})(?:$|[\s_\-.()\[\]])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LabelledPattern = new(
        @"(?:^|[\s_\-.()\[\]])(?<kind>part|pt|segment|seg|disc|disk|cd|hour|hr)[\s_\-.]*(?<part>\d{1,2})(?:$|[\s_\-.()\[\]])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LetterPattern = new(
        @"(?:^|[\s_\-.()\[\]])(?:(?:part|pt|segment|seg)[\s_\-.]*)?(?<letter>[ab])(?:[\s_\-.()\[\]]*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex LabelledRomanPattern = new(
        @"(?:^|[\s_\-.()\[\]])(?<kind>part|pt|segment|seg|disc|disk|cd|hour|hr)[\s_\-.]*(?<roman>i{1,3}|iv|v|vi{0,3}|ix|x)(?:[\s_\-.()\[\]]*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // Bare Roman numerals are deliberately case-sensitive. This recognises
    // archive suffixes such as “I” and “II” without treating an ordinary
    // trailing word “i” as a multipart marker.
    private static readonly Regex BareRomanPattern = new(
        @"(?:^|[\s_\-.()\[\]])(?<roman>I{1,3}|IV|V|VI{0,3}|IX|X)(?:[\s_\-.()\[\]]*)$",
        RegexOptions.CultureInvariant);

    public static MultipartMatch Detect(string? filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return MultipartMatch.None;

        var match = LabelledOfPattern.Match(filename);
        if (TryCreate(match, out var result, hasTotal: true)) return result;

        match = BareOfPattern.Match(filename);
        if (match.Success && int.TryParse(match.Groups["part"].Value, out var barePart) &&
            int.TryParse(match.Groups["total"].Value, out var bareTotal) &&
            barePart > 0 && bareTotal >= barePart && bareTotal <= 99)
            return new MultipartMatch(barePart, bareTotal, "Part", "Recognised an unlabelled ‘x of y’ multipart suffix.", match.Value.Trim());

        match = LabelledPattern.Match(filename);
        if (TryCreate(match, out result, hasTotal: false)) return result;

        match = LabelledRomanPattern.Match(filename);
        if (match.Success && TryParseRoman(match.Groups["roman"].Value, out var labelledRomanPart))
            return new MultipartMatch(labelledRomanPart, null, NormalizeKind(match.Groups["kind"].Value),
                "Recognised a labelled Roman-numeral multipart suffix.", match.Value.Trim());

        match = BareRomanPattern.Match(filename);
        if (match.Success && TryParseRoman(match.Groups["roman"].Value, out var romanPart))
            return new MultipartMatch(romanPart, null, "Part",
                "Recognised a bare Roman-numeral multipart suffix.", match.Value.Trim());

        match = LetterPattern.Match(filename);
        if (match.Success)
        {
            var letter = match.Groups["letter"].Value;
            var part = letter.Equals("b", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            return new MultipartMatch(part, 2, "Part", "Recognised an A/B multipart suffix.", match.Value.Trim());
        }

        return MultipartMatch.None;
    }

    private static bool TryParseRoman(string value, out int result)
    {
        result = value.Trim().ToUpperInvariant() switch
        {
            "I" => 1,
            "II" => 2,
            "III" => 3,
            "IV" => 4,
            "V" => 5,
            "VI" => 6,
            "VII" => 7,
            "VIII" => 8,
            "IX" => 9,
            "X" => 10,
            _ => 0
        };
        return result > 0;
    }

    private static bool TryCreate(Match match, out MultipartMatch result, bool hasTotal)
    {
        result = MultipartMatch.None;
        if (!match.Success || !int.TryParse(match.Groups["part"].Value, out var part) || part <= 0) return false;
        int? total = null;
        if (hasTotal)
        {
            if (!int.TryParse(match.Groups["total"].Value, out var parsedTotal) || parsedTotal < part || parsedTotal > 99) return false;
            total = parsedTotal;
        }
        result = new MultipartMatch(part, total, NormalizeKind(match.Groups["kind"].Value),
            total.HasValue ? "Recognised a labelled multipart ‘x of y’ marker." : "Recognised a labelled multipart marker.",
            match.Value.Trim());
        return true;
    }

    private static string NormalizeKind(string kind) => kind.ToLowerInvariant() switch
    {
        "hour" or "hr" => "Hour",
        "segment" or "seg" => "Segment",
        "disc" or "disk" or "cd" => "Disc",
        _ => "Part"
    };
}

public sealed record MultipartMatch(int PartNumber, int? TotalParts, string? Kind, string Reasoning, string MatchedText)
{
    public static MultipartMatch None { get; } = new(1, null, null, "No multipart marker was found.", string.Empty);
    public bool IsMultipart => PartNumber > 1 || TotalParts.HasValue || !string.IsNullOrWhiteSpace(Kind);
}
