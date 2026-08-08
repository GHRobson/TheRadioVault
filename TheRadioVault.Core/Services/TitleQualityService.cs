using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace TheRadioVault.Core.Services;

/// <summary>
/// Determines whether optional descriptive text is useful as an archive
/// headline. Show + broadcast date remain the permanent broadcast identity.
/// </summary>
public static partial class TitleQualityService
{
    private static readonly HashSet<string> GenericTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "episode", "regular episode", "archive episode", "show", "full show", "radio show",
        "track", "track 1", "audio", "unknown", "unknown title", "untitled", "no title", "podcast"
    };

    public static bool IsMeaningful(string? headline, string? collectionName, string? originalFilename)
    {
        var value = CleanCandidate(headline, collectionName);
        if (value.Length < 3 || GenericTitles.Contains(value)) return false;
        if (DateOnlyRegex().IsMatch(value)) return false;

        var normalizedTitle = Normalize(value);
        var normalizedCollection = Normalize(collectionName ?? "");
        if (normalizedTitle == normalizedCollection) return false;

        if (!string.IsNullOrWhiteSpace(originalFilename))
        {
            var filename = Path.GetFileNameWithoutExtension(originalFilename);
            if (Normalize(filename) == Normalize(headline ?? string.Empty) &&
                CleanCandidate(filename, collectionName).Length < 3)
                return false;
        }

        return true;
    }

    public static string DisplayHeadline(string? rawHeadline, string? collectionName, string? originalFilename)
    {
        var cleaned = CleanCandidate(rawHeadline, collectionName);
        return IsMeaningful(cleaned, collectionName, originalFilename) ? cleaned : string.Empty;
    }

    // Compatibility name retained for existing WPF bindings.
    public static string SmartEpisodeTitle(string? rawTitle, string? collectionName, string? originalFilename)
        => DisplayHeadline(rawTitle, collectionName, originalFilename);

    public static string DashboardTitle(string? rawHeadline, string? collectionName, string? originalFilename, DateTime? airDate)
    {
        var headline = DisplayHeadline(rawHeadline, collectionName, originalFilename);
        if (!string.IsNullOrWhiteSpace(headline)) return headline;
        if (airDate.HasValue) return airDate.Value.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture);
        return string.IsNullOrWhiteSpace(collectionName) ? "Undated broadcast" : collectionName;
    }

    private static string CleanCandidate(string? rawHeadline, string? collectionName)
    {
        if (string.IsNullOrWhiteSpace(rawHeadline)) return string.Empty;

        var value = rawHeadline;
        value = DateInFilenameRegex().Replace(value, " ");
        value = NamedDateRegex().Replace(value, " ");
        value = WeekdayRegex().Replace(value, " ");
        value = TechnicalTextRegex().Replace(value, " ");
        value = CollectionTextRegex().Replace(value, " ");
        value = StationDictionary.RemoveRecognisedStationText(value);
        value = SegmentTextRegex().Replace(value, " ");
        value = ArchiveCodeRegex().Replace(value, " ");
        value = CopyNoiseRegex().Replace(value, " ");
        value = Regex.Replace(value, @"(?<=\p{L})[.](?=\p{L})", " ");
        value = Regex.Replace(value, @"[_|]+", " ");
        value = Regex.Replace(value, @"\s*[-–—:]+\s*", " ");
        return Collapse(value);
    }

    private static string Collapse(string value)
        => Regex.Replace(value.Trim(), @"\s+", " ").Trim(' ', '-', '_', '.', '|');

    private static string Normalize(string value)
        => Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]+", "");

    [GeneratedRegex(@"^(?:monday|tuesday|wednesday|thursday|friday|saturday|sunday)?\s*,?\s*\d{1,2}\s+(?:january|february|march|april|may|june|july|august|september|october|november|december)\s+\d{4}$", RegexOptions.IgnoreCase)]
    private static partial Regex DateOnlyRegex();

    [GeneratedRegex(@"\b(?:\d{2,3}\s*k(?:bps)?|\d{2,3}\s*kbps|mp3|m4a|aac|flac|ogg|wav|wma|stereo|mono|vbr|cbr|hq|high quality|low quality|remaster(?:ed)?|re-?encode(?:d)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalTextRegex();

    [GeneratedRegex(@"\b(?:rbi|ron\s*bennington\s*interviews?|(?:(?:the\s+)?ron\s*(?:and|&)\s*ron(?:\s+show)?|ronronshow)|(?:unmasked|unmaked)(?:\s+with\s+ron\s+bennington)?|(?:the\s+)?(?:ron\s*(?:and|&)\s*fez|ron\s+fez|ronfez|raf|bennington|benningtoon|benningotn|opie\s*(?:and|&)\s*anthony|o\s*&?\s*a|r\s*&?\s*f))\b", RegexOptions.IgnoreCase)]
    private static partial Regex CollectionTextRegex();

    [GeneratedRegex(@"\b(?:part|pt|segment|seg|hour|hr|disc|disk|cd)?\s*\d{1,2}\s*(?:of|/)\s*\d{1,2}|(?:hour|hr|part|pt|segment|seg|disc|disk|cd)\s*[0-9a-z]{1,2}\b|(?:^|[\s_\-.()\[\]])[ab](?:$|[\s_\-.()\[\]])", RegexOptions.IgnoreCase)]
    private static partial Regex SegmentTextRegex();

    [GeneratedRegex(@"(?<!\d)(?:19|20)\d{2}[-._ /](?:0?[1-9]|1[0-2])[-._ /](?:0?[1-9]|[12]\d|3[01])(?!\d)|(?<!\d)(?:19|20)\d{6}(?!\d)|(?<!\d)(?:0?[1-9]|1[0-2])[-._ /](?:0?[1-9]|[12]\d|3[01])[-._ /](?:19|20)\d{2}(?!\d)")]
    private static partial Regex DateInFilenameRegex();

    [GeneratedRegex(@"\b(?:mon(?:day)?|tue(?:s|sday)?|wed(?:nesday)?|thu(?:rs|rsday)?|fri(?:day)?|sat(?:urday)?|sun(?:day)?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WeekdayRegex();

    [GeneratedRegex(@"\b(?:0?[1-9]|[12]\d|3[01])(?:st|nd|rd|th)?[\s._,-]+(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)[\s._,-]+(?:19|20)?\d{2}\b|\b(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)[\s._,-]+(?:0?[1-9]|[12]\d|3[01])(?:st|nd|rd|th)?(?:,)?[\s._,-]+(?:19|20)?\d{2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex NamedDateRegex();

    [GeneratedRegex(@"\b(?:raf|r&f|rf)\d{3,6}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArchiveCodeRegex();

    [GeneratedRegex(@"\b(?:episode|regular\s+episode|archive\s+episode|full\s+show|radio\s+show|copy|download(?:ed)?|partial|take[\s._-]*\d+)\b|\(\d+\)$", RegexOptions.IgnoreCase)]
    private static partial Regex CopyNoiseRegex();

    [GeneratedRegex(@"\b(?:raf|r&f|rf)[\s._-]*(?:0[1-9]|1[0-2])(?:0[1-9]|[12]\d|3[01])[\s._-]*\d{2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex CompactRonFezDateRegex();
}
