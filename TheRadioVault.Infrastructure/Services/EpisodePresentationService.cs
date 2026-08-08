namespace TheRadioVault.Services;

public static class EpisodePresentationService
{
    private static readonly string[] Boilerplate =
    {
        "No sufficiently reliable public segment index was recovered",
        "the entry is intentionally conservative",
        "The surviving research index does not provide a reliable segment-by-segment rundown",
        "Managed by The Radio Vault",
        "Broadcast ID:",
        "Original filename:"
    };

    public static string SummaryTeaser(string? value)
    {
        var text = (value ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (text.Length == 0 || Boilerplate.Any(x => text.Contains(x, StringComparison.OrdinalIgnoreCase))) return "";
        while (text.Contains("  ")) text = text.Replace("  ", " ");
        return text.Length <= 180 ? text : text[..177].TrimEnd() + "…";
    }

    public static string DiscoveryLine(string? guests, string? topics, string? station, string? slot)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(guests)) parts.Add("Featuring " + FirstItems(guests, 2));
        if (!string.IsNullOrWhiteSpace(topics)) parts.Add(FirstItems(topics, 3));
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(station)) parts.Add(station.Trim());
        if (parts.Count == 0 && !string.IsNullOrWhiteSpace(slot)) parts.Add(slot.Trim());
        return string.Join("  ·  ", parts);
    }

    public static string ContextBadge(string? station, string? slot, string? multipart)
    {
        if (!string.IsNullOrWhiteSpace(multipart)) return multipart;
        if (!string.IsNullOrWhiteSpace(station)) return station.Trim();
        return string.IsNullOrWhiteSpace(slot) ? "" : slot.Trim();
    }

    private static string FirstItems(string value, int count)
        => string.Join(", ", value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).Take(count));
}
