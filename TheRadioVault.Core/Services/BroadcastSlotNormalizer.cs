using System.Text;

namespace TheRadioVault.Core.Services;

/// <summary>
/// Converts the many archive labels used for the same broadcast slot into a
/// stable comparison token. Display labels remain unchanged in the database.
/// </summary>
public static class BroadcastSlotNormalizer
{
    public static bool Equivalent(string? left, string? right)
        => string.Equals(Canonicalize(left), Canonicalize(right), StringComparison.OrdinalIgnoreCase);

    public static string Canonicalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var loose = NormalizeLoose(value);
        if (loose.Length == 0) return string.Empty;

        if (loose is "regular" or "regularshow" or "standard" or "standardshow" or "main" or "mainshow" or "primary")
            return string.Empty;

        if (loose.Contains("opieradio", StringComparison.Ordinal) || loose == "or")
            return "opieradio";

        // Imported research sometimes stores the scheduled clock range rather
        // than a simple AM/PM label, for example “12:00 p.m.–3:00 p.m. Eastern”.
        // Treat a range containing only one meridiem as that same slot family.
        var lower = value.Trim().ToLowerInvariant();
        var hasAm = lower.Contains("a.m.", StringComparison.Ordinal)
                    || lower.Contains(" am", StringComparison.Ordinal)
                    || lower.EndsWith("am", StringComparison.Ordinal);
        var hasPm = lower.Contains("p.m.", StringComparison.Ordinal)
                    || lower.Contains(" pm", StringComparison.Ordinal)
                    || lower.EndsWith("pm", StringComparison.Ordinal);
        if (hasPm && !hasAm) return "pm";
        if (hasAm && !hasPm) return "am";

        if (loose is "am" or "morning" or "morningshow")
            return "am";

        // Archive filenames use PM, afternoon and evening interchangeably for
        // the later same-day Ron & Fez recording.
        if (loose is "pm" or "afternoon" or "afternoonshow" or "evening" or "eveningshow" or "eve")
            return "pm";

        if (loose is "mid" or "midday" or "middayshow" or "noon" or "noonshow" or "lunchtime")
            return "midday";

        if (loose is "late" or "lateshow")
            return "late";

        return loose;
    }

    private static string NormalizeLoose(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim().ToLowerInvariant())
            if (char.IsLetterOrDigit(character)) builder.Append(character);
        return builder.ToString();
    }
}
