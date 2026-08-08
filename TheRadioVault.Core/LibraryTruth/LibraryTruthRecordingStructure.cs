using System.Text.RegularExpressions;

namespace TheRadioVault.Core.LibraryTruth;

public enum LibraryTruthNumericTokenKind
{
    None,
    ExplicitMultipart,
    LeadingTrackNumber,
    AmbiguousTrailingNumber,
    ExplicitVariant
}

public sealed record LibraryTruthRecordingFilenameStructure(
    string FamilyKey,
    string? VariantToken,
    int? AmbiguousTrailingNumber,
    LibraryTruthNumericTokenKind NumericTokenKind,
    IReadOnlySet<string> ProgrammeTokens,
    string SourceStyle,
    string EncodingKey);

/// <summary>
/// Alpha7 filename-family evidence used only by the shadow Library Truth engine.
/// It deliberately separates source/variant lineages before multipart segments
/// are assembled, and leaves lone trailing numbers ambiguous until sibling
/// filenames establish a contiguous sequence.
/// </summary>
public static class LibraryTruthRecordingStructure
{
    private static readonly Regex Variant = Rx(@"(?:^|[\s_.-])(?:v|ver|version)[\s_.-]*(?<number>\d{1,3})(?=$|[\s_.-])");
    private static readonly Regex ExplicitPart = Rx(@"(?:^|[\s_.-])(?:part|pt|hour|hr|segment|seg|disc|disk|cd|p)[\s_.-]*(?<number>\d{1,3})(?:[\s_.-]*(?:of|/)[\s_.-]*(?<total>\d{1,3}))?(?=$|[\s_.-])");
    private static readonly Regex OfTotal = Rx(@"(?:^|[\s_.-])(?<number>\d{1,3})\s*(?:of|/)\s*(?<total>\d{1,3})(?=$|[\s_.-])");
    private static readonly Regex TrailingRomanOrLetter = Rx(@"(?:^|[\s_.-])(?:I{1,3}|IV|V|VI{0,3}|IX|X|A|B)\s*$");
    private static readonly Regex AttachedLetterPart = Rx(@"(?<=\d)[ab]\s*$");
    private static readonly Regex BenignMultipartAnnotation = Rx(@"(?:^|[\s_.-])(?:partial(?:ly)?|ph)(?=$|[\s_.-])");
    private static readonly Regex BareTrailingNumber = Rx(@"(?:^|[\s_.-])(?<number>\d{1,3})\s*$");
    private static readonly Regex LeadingNumber = Rx(@"^\s*\d{1,3}(?=$|[\s_.-])");
    private static readonly Regex StructuredDate = Rx(@"\b(?:19|20)\d{2}[\s_.-]\d{1,2}[\s_.-]\d{1,2}\b|\b\d{1,2}[\s_.-]\d{1,2}[\s_.-](?:19|20)?\d{2}\b");
    private static readonly Regex CompactDate = Rx(@"\b(?:19|20)\d{6}\b");
    private static readonly Regex NamedMonthDate = Rx(@"\b(?:\d{1,2}(?:st|nd|rd|th)?[\s,._-]+)?(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)(?:[\s,._-]+\d{1,2}(?:st|nd|rd|th)?)?[\s,._-]+(?:19|20)\d{2}\b");
    private static readonly Regex EncodingToken = Rx(@"\b(?:(?:cf|vbr)?\d{2,3}k|hq|lq|mono|stereo)\b");

    private static readonly string[] MonthNames =
    {
        "jan", "january", "feb", "february", "mar", "march", "apr", "april",
        "may", "jun", "june", "jul", "july", "aug", "august", "sep", "sept",
        "september", "oct", "october", "nov", "november", "dec", "december"
    };

    public static LibraryTruthRecordingFilenameStructure Analyse(string originalFilename, bool parserFoundExplicitMultipart)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalFilename);

        var stem = Path.GetFileNameWithoutExtension(originalFilename).Trim();
        var variantMatch = Variant.Match(stem);
        var variant = variantMatch.Success ? $"V{variantMatch.Groups["number"].Value}" : null;
        var leadingTrack = IsLeadingTrackNumberBeforeDate(stem);

        int? ambiguousTrailing = null;
        if (!parserFoundExplicitMultipart && !leadingTrack)
        {
            var withoutDate = RemoveDateTokens(stem);
            var trailing = BareTrailingNumber.Match(withoutDate);
            if (trailing.Success && int.TryParse(trailing.Groups["number"].Value, out var number) && number is >= 1 and <= 99)
                ambiguousTrailing = number;
        }

        var sourceStyle = DetectSourceStyle(stem);
        var encodingKey = DetectEncodingKey(stem, originalFilename);
        var familyKey = BuildFamilyKey(stem, sourceStyle, encodingKey, variant, parserFoundExplicitMultipart);
        var tokenKind = parserFoundExplicitMultipart
            ? LibraryTruthNumericTokenKind.ExplicitMultipart
            : leadingTrack
                ? LibraryTruthNumericTokenKind.LeadingTrackNumber
                : ambiguousTrailing.HasValue
                    ? LibraryTruthNumericTokenKind.AmbiguousTrailingNumber
                    : variant is not null
                        ? LibraryTruthNumericTokenKind.ExplicitVariant
                        : LibraryTruthNumericTokenKind.None;

        return new LibraryTruthRecordingFilenameStructure(
            familyKey,
            variant,
            ambiguousTrailing,
            tokenKind,
            DetectProgrammeTokens(stem),
            sourceStyle,
            encodingKey);
    }

    private static string BuildFamilyKey(
        string stem,
        string sourceStyle,
        string encodingKey,
        string? variant,
        bool parserFoundExplicitMultipart)
    {
        var text = stem.ToLowerInvariant();
        if (parserFoundExplicitMultipart)
        {
            // Some archive dates run directly into an A/B segment suffix
            // (for example 10-31-2002a). Remove that segment marker before
            // stripping the date so both halves retain one filename family.
            text = AttachedLetterPart.Replace(text, " ");
        }
        text = RemoveDateTokens(text);
        text = Variant.Replace(text, " ");
        text = ExplicitPart.Replace(text, " ");
        text = OfTotal.Replace(text, " ");
        if (parserFoundExplicitMultipart)
        {
            text = TrailingRomanOrLetter.Replace(text, " ");
            // Quality/state suffixes describe one segment, not a separate
            // recording lineage. Programme markers such as FNR and Eddie
            // Trunk are intentionally not part of this list.
            text = BenignMultipartAnnotation.Replace(text, " ");
        }
        text = BareTrailingNumber.Replace(text, " ");
        text = Regex.Replace(text,
            @"(?<![a-z0-9])(?:ron\s*(?:and|&)\s*fez|r\s*&\s*f|r\s*and\s*f|raf|rf)(?![a-z0-9])",
            " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = Regex.Replace(text, @"\b(?:am|pm|morning|afternoon|evening|show|broadcast|archive|recording)\b", " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        text = EncodingToken.Replace(text, " ");
        text = Regex.Replace(text, @"[^a-z0-9]+", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim();
        if (text.Length == 0) text = "recording";

        var variantKey = variant?.ToLowerInvariant() ?? "no-variant";
        return $"{sourceStyle}|{text}|{encodingKey}|{variantKey}";
    }

    private static string DetectSourceStyle(string stem)
    {
        if (Regex.IsMatch(stem, @"(?<![a-z0-9])fnr(?![a-z0-9])|friday\s+night\s+rocks", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "fnr";
        if (Regex.IsMatch(stem, @"(?<![a-z0-9])r\s*&\s*f(?![a-z0-9])|(?<![a-z0-9])raf(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "short-rf";
        if (Regex.IsMatch(stem, @"(?<![a-z0-9])ron\s*(?:and|&)\s*fez(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            return "long-ron-fez";
        return "other";
    }

    private static string DetectEncodingKey(string stem, string filename)
    {
        var tokens = EncodingToken.Matches(stem).Cast<Match>().Select(x => x.Value.ToLowerInvariant()).Distinct().OrderBy(x => x).ToArray();
        var extension = Path.GetExtension(filename).TrimStart('.').ToLowerInvariant();
        return tokens.Length == 0 ? extension : $"{extension}:{string.Join("-", tokens)}";
    }

    private static IReadOnlySet<string> DetectProgrammeTokens(string text)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(tokens, text, "fnr", "fnr");
        Add(tokens, text, "friday night rocks", "fnr");
        Add(tokens, text, "eddie trunk", "eddie-trunk");
        Add(tokens, text, "afro", "afro");
        Add(tokens, text, "mini afro", "mini-afro");
        Add(tokens, text, "worst of", "worst-of");
        Add(tokens, text, "best of", "best-of");
        Add(tokens, text, "replay", "replay");
        return tokens;
    }

    private static void Add(ISet<string> target, string text, string needle, string token)
    {
        if (text.Contains(needle, StringComparison.OrdinalIgnoreCase)) target.Add(token);
    }

    private static bool IsLeadingTrackNumberBeforeDate(string text)
    {
        var leading = LeadingNumber.Match(text);
        if (!leading.Success) return false;
        var remainder = text[leading.Length..].TrimStart(' ', '-', '_', '.');
        var hasNamedMonth = MonthNames.Any(month => Regex.IsMatch(remainder, $@"\b{Regex.Escape(month)}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        var hasOrdinalDay = Regex.IsMatch(remainder, @"\b\d{1,2}(?:st|nd|rd|th)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return hasNamedMonth || hasOrdinalDay || StructuredDate.IsMatch(remainder) || CompactDate.IsMatch(remainder);
    }

    private static string RemoveDateTokens(string text)
    {
        var result = NamedMonthDate.Replace(text, " ");
        result = StructuredDate.Replace(result, " ");
        return CompactDate.Replace(result, " ");
    }

    private static Regex Rx(string pattern)
        => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
