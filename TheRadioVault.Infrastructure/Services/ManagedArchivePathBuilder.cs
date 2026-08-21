namespace TheRadioVault.Services;

internal static class ManagedArchivePathBuilder
{
    public static string Build(
        string root,
        string? showName,
        DateOnly airDate,
        string? broadcastSlot,
        string? title,
        int partNumber,
        int? totalParts,
        int recordingFileCount,
        string extension)
    {
        var show = SafeComponent(showName, "Unknown Show", 60);
        var slot = SafeComponent(broadcastSlot, string.Empty, 32);
        var headline = SafeComponent(title, string.Empty, 64);
        var name = $"{airDate:yyyy-MM-dd} - {show}";
        if (!string.IsNullOrWhiteSpace(slot)) name += $" - {slot}";
        if (!string.IsNullOrWhiteSpace(headline) && !headline.Equals(show, StringComparison.OrdinalIgnoreCase))
            name += $" - {headline}";
        if (totalParts is > 1 || recordingFileCount > 1)
            name += $" - Part {Math.Max(1, partNumber):00}";
        name = SafeComponent(name, $"{airDate:yyyy-MM-dd} - {show}", 150);
        return Path.Combine(
            Path.GetFullPath(root),
            show,
            airDate.Year.ToString("0000"),
            airDate.ToString("yyyy-MM"),
            name + NormalizeExtension(extension));
    }

    public static string BuildUndated(string root, string? showName, string fileName)
    {
        var show = SafeComponent(showName, "Unknown Show", 60);
        var name = SafeComponent(Path.GetFileNameWithoutExtension(fileName), "RSS broadcast", 150);
        return Path.Combine(Path.GetFullPath(root), show, "Undated", name + NormalizeExtension(Path.GetExtension(fileName)));
    }

    public static string SafeComponent(string? value, string fallback, int maximumLength)
    {
        var candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        foreach (var character in Path.GetInvalidFileNameChars()) candidate = candidate.Replace(character, '-');
        candidate = candidate.Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(candidate)) candidate = fallback;
        return candidate.Length <= maximumLength ? candidate : candidate[..maximumLength].TrimEnd(' ', '.');
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension)) return string.Empty;
        return (extension.StartsWith('.') ? extension : "." + extension).ToLowerInvariant();
    }
}
