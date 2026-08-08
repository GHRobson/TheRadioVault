using System.Text.RegularExpressions;

namespace TheRadioVault.Core.Services;

/// <summary>
/// Resolves common radio-station and channel aliases to stable display names.
/// Detection is deliberately token-based so short aliases such as XM do not
/// match inside unrelated words.
/// </summary>
public static class StationDictionary
{
    private sealed record StationRule(string CanonicalName, string Pattern, string Confidence, string Description);

    private static readonly StationRule[] Rules =
    {
        // Prefer a named channel over its wider platform when both occur.
        new("Opie & Anthony Channel", @"(?:^|[^a-z0-9])(?:opie\s*(?:&|and)\s*anthony\s*channel|o\s*&\s*a\s*channel)(?:$|[^a-z0-9])", "High", "Recognised an Opie & Anthony Channel alias."),
        new("Raw Dog", @"(?:^|[^a-z0-9])raw[\s._-]*dog(?:$|[^a-z0-9])", "High", "Recognised a Raw Dog channel alias."),
        new("The Virus", @"(?:^|[^a-z0-9])(?:the[\s._-]*)?virus(?:$|[^a-z0-9])", "High", "Recognised a Virus channel alias."),
        new("Faction", @"(?:^|[^a-z0-9])faction(?:$|[^a-z0-9])", "High", "Recognised a Faction channel alias."),
        new("102.7 WNEW", @"(?:^|[^a-z0-9])(?:102[\s._-]*7[\s._-]*)?wnew(?:[\s._-]*fm)?(?:$|[^a-z0-9])", "High", "Recognised a WNEW alias."),
        new("Free FM", @"(?:^|[^a-z0-9])free[\s._-]*fm(?:$|[^a-z0-9])", "High", "Recognised a Free FM alias."),
        new("SiriusXM", @"(?:^|[^a-z0-9])sirius[\s._-]*xm(?:\s*radio)?(?:$|[^a-z0-9])", "High", "Recognised a SiriusXM alias."),
        new("Sirius Satellite Radio", @"(?:^|[^a-z0-9])sirius(?:[\s._-]*satellite(?:[\s._-]*radio)?)?(?:$|[^a-z0-9])", "High", "Recognised a Sirius Satellite Radio alias."),
        new("XM Satellite Radio", @"(?:^|[^a-z0-9])(?:xm(?:[\s._-]*(?:satellite(?:[\s._-]*radio)?|radio|talk|202))?|xm202)(?:$|[^a-z0-9])", "High", "Recognised an XM Satellite Radio alias."),
        new("Classic Replay", @"(?:^|[^a-z0-9])classic[\s._-]*replay(?:$|[^a-z0-9])", "Probable", "Recognised a Classic Replay label.")
    };
    public static StationMatch? Detect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        foreach (var rule in Rules)
        {
            var match = Regex.Match(value, rule.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            if (match.Success)
                return new StationMatch(rule.CanonicalName, rule.Confidence, rule.Description, match.Index, match.Length);
        }
        return null;
    }

    public static string? Normalize(string? value)
        => Detect(value)?.CanonicalName ?? (string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    public static string RemoveRecognisedStationText(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        foreach (var rule in Rules)
            value = Regex.Replace(value, rule.Pattern, " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return value;
    }
}

public sealed record StationMatch(
    string CanonicalName,
    string Confidence,
    string Reasoning,
    int Index,
    int Length);
