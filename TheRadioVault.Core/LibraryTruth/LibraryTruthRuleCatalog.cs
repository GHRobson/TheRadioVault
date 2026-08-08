using System.Text.RegularExpressions;

namespace TheRadioVault.Core.LibraryTruth;

public sealed record LibraryTruthCollectionRule(string CanonicalName, IReadOnlyList<Regex> Aliases);

public static class LibraryTruthRuleCatalog
{
    // Specific spin-offs are checked before broader parent-show aliases.
    public static IReadOnlyList<LibraryTruthCollectionRule> Collections { get; } = new[]
    {
        Rule("AFRO Show",
            @"\bmini\s+afro\s+sho(?:w)?\b",
            @"\bafro\s+sho(?:w)?\b",
            @"\bafro\b"),
        Rule("Ron Bennington Interviews",
            @"\bron\s*bennington\s*interviews?\b",
            @"\bronbenningtoninterviews?\b",
            @"(?:^|[\\/])rbi(?=$|[\\/\s._-])",
            @"\bron\s+interviews?\b"),
        Rule("The Ron & Ron Show",
            @"\b(?:the\s+)?ron\s*(?:&|and)\s*ron(?:\s+show)?\b",
            @"\bronronshow\b"),
        Rule("Unmasked",
            @"(?:^|[\\/])(?:siriusxm\s+)?(?:unmasked|unmaked)(?:\s+with\s+ron\s+bennington)?(?=$|[\s\\/])"),
        Rule("Opie & Anthony",
            @"\bopie\s*(?:&|and)\s*anthony\b",
            @"\bo\s*&\s*a\b",
            @"\boanda\b"),
        Rule("Bennington",
            @"\bthe\s+bennington\s+show\b",
            @"\bbennington\b",
            @"\bbenningotn\b"),
        Rule("Ron & Fez",
            @"\bron\s*(?:&|and)\s*fez\b",
            @"\br\s*&\s*f\b",
            @"\bronfez\b",
            @"\braf(?=\d|\b)")
    };

    public static IReadOnlyList<Regex> TechnicalNoisePatterns { get; } = new[]
    {
        Rx(@"\b(?:cf|cfr)?\s*\d{2,3}\s*k(?:bps)?\b"),
        Rx(@"\b\d{2,3}\s*kbps\b"),
        Rx(@"\b(?:mp3|m4a|aac|flac|wav|wma|ogg)\b"),
        Rx(@"\b(?:stereo|mono|hq|lq|webrip|web\s*rip|archive|broadcast|recording|rip)\b"),
        Rx(@"\b(?:mon|monday|tue|tues|tuesday|wed|weds|wednesday|thu|thur|thurs|thursday|fri|friday|sat|saturday|sun|sunday)\b"),
        Rx(@"\b(?:regular\s+show|regular|standard\s+show|standard)\b"),
        Rx(@"\b(?:no\s+commercials?|commercial\s*free|full\s+show|complete\s+show|partial(?:ly)?|free\s*fm)\b"),
        Rx(@"(?:^|[\s_.\-])v\d+(?:$|[\s_.\-])"),
        Rx(@"\((?:copy|duplicate|dupe|alt|alternate|reencode|re-encode)\s*\d*\)"),
        Rx(@"\(dot\s*com\)"),
        Rx(@"\(\d+\)$")
    };

    public static Regex CreateAliasRegex(string pattern) => Rx(pattern);

    private static LibraryTruthCollectionRule Rule(string name, params string[] aliases)
        => new(name, aliases.Select(Rx).ToArray());

    private static Regex Rx(string pattern)
        => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
