namespace TheRadioVault.Core.Services;

/// <summary>
/// Defines which episode dates are durable user/research decisions and which
/// may still be refined by automated filename parsing. Keeping this policy in
/// Core prevents the scanner, Research workspace and Library Truth engine from
/// assigning different meanings to the same date-confidence value.
/// </summary>
public static class DateConfidencePolicy
{
    public static bool IsResearchBacked(string? value)
    {
        var confidence = value?.Trim() ?? string.Empty;
        return confidence.StartsWith("Research exact date", StringComparison.OrdinalIgnoreCase)
            || confidence.StartsWith("Research authoritative", StringComparison.OrdinalIgnoreCase)
            || confidence.StartsWith("Research manual", StringComparison.OrdinalIgnoreCase)
            || confidence.StartsWith("Research date approved", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsProtectedFromAutomatedParsing(string? value)
    {
        var confidence = value?.Trim() ?? string.Empty;
        return confidence.Equals("Manual", StringComparison.OrdinalIgnoreCase)
            || confidence.Equals("Confirmed", StringComparison.OrdinalIgnoreCase)
            || IsResearchBacked(confidence);
    }

    public static bool IsTrustedForLibraryIdentity(string? value)
    {
        var confidence = value?.Trim() ?? string.Empty;
        return confidence.Equals("High", StringComparison.OrdinalIgnoreCase)
            || IsProtectedFromAutomatedParsing(confidence);
    }

    public static bool IsUncertain(string? value)
        => !IsTrustedForLibraryIdentity(value);
}
