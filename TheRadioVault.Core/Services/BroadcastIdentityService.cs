using System.Text;
using TheRadioVault.Core.Domain;

namespace TheRadioVault.Core.Services;

public static class BroadcastIdentityService
{
    public static string CreateStableId(string collectionName, DateOnly? airDate, int partNumber = 1, string? broadcastSlot = null)
    {
        var slug = Slugify(string.IsNullOrWhiteSpace(collectionName) ? "Broadcast" : collectionName);
        var date = airDate?.ToString("yyyy-MM-dd") ?? "UNKNOWN";
        var slot = string.IsNullOrWhiteSpace(broadcastSlot) ? string.Empty : $"-{Slugify(broadcastSlot)}";
        var part = partNumber > 1 ? $"-P{partNumber}" : string.Empty;
        return $"{slug}-{date}{slot}{part}";
    }

    public static BroadcastIdentity From(string collectionName, DateTime? airDate, int partNumber = 1)
        => new(collectionName, airDate.HasValue ? DateOnly.FromDateTime(airDate.Value) : null, Math.Max(1, partNumber));

    private static string Slugify(string value)
    {
        var builder = new StringBuilder(value.Length);
        var previousDash = false;
        foreach (var character in value.ToUpperInvariant())
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(character);
                previousDash = false;
            }
            else if (!previousDash && builder.Length > 0)
            {
                builder.Append('-');
                previousDash = true;
            }
        }
        return builder.ToString().Trim('-');
    }
}
