using System.Text;

namespace TheRadioVault.Core.Services;

/// <summary>
/// Conservative identity rules for preventing the same saved Moment from being
/// inserted repeatedly by imports, rescans, canonical remapping or repeated UI
/// commands. Moments with different wording or more than two seconds apart are
/// always preserved as distinct user data.
/// </summary>
public static class MomentDeduplicationPolicy
{
    public const long PositionToleranceMs = 2_000;

    public static bool IsEquivalent(
        string canonicalIdentityA,
        long positionMsA,
        string? titleA,
        string? notesA,
        string canonicalIdentityB,
        long positionMsB,
        string? titleB,
        string? notesB)
    {
        if (!string.Equals(canonicalIdentityA, canonicalIdentityB, StringComparison.OrdinalIgnoreCase)) return false;
        if (Math.Abs(Math.Max(0, positionMsA) - Math.Max(0, positionMsB)) > PositionToleranceMs) return false;
        if (!string.Equals(NormalizeText(titleA), NormalizeText(titleB), StringComparison.Ordinal)) return false;
        return string.Equals(NormalizeText(notesA), NormalizeText(notesB), StringComparison.Ordinal);
    }

    public static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }
}
