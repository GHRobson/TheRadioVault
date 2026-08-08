using TheRadioVault.Media.Contracts;
using TheRadioVault.Media.Models;

namespace TheRadioVault.Media.Services;

public sealed class MediaRenamePlanner : IMediaRenamePlanner
{
    public RenamePlan Plan(string currentPath, string collectionName, DateTime? airDate, string? title, int partNumber = 1)
    {
        var directory = Path.GetDirectoryName(currentPath) ?? string.Empty;
        var extension = Path.GetExtension(currentPath);
        var date = airDate?.ToString("yyyy-MM-dd") ?? "Unknown date";
        var titlePart = string.IsNullOrWhiteSpace(title) ? string.Empty : $" - {Sanitize(title)}";
        var part = partNumber > 1 ? $" - Part {partNumber}" : string.Empty;
        var proposed = Path.Combine(directory, $"{date} - {Sanitize(collectionName)}{titlePart}{part}{extension}");
        return new RenamePlan(currentPath, proposed, !string.Equals(currentPath, proposed, StringComparison.OrdinalIgnoreCase));
    }

    public string FindAvailablePath(string proposedPath, string currentPath)
    {
        if (string.Equals(proposedPath, currentPath, StringComparison.OrdinalIgnoreCase) || !File.Exists(proposedPath)) return proposedPath;
        var directory = Path.GetDirectoryName(proposedPath) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(proposedPath);
        var extension = Path.GetExtension(proposedPath);
        for (var number = 2; ; number++)
        {
            var candidate = Path.Combine(directory, $"{name} ({number}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    private static string Sanitize(string value)
    {
        foreach (var character in Path.GetInvalidFileNameChars()) value = value.Replace(character, '-');
        return value.Trim();
    }
}
