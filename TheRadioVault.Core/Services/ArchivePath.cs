namespace TheRadioVault.Core.Services;

/// <summary>
/// Reads persisted archive paths independently of the operating system that
/// originally indexed them. Radio Vault databases can legitimately move
/// between Windows, macOS and Linux, so stored separators cannot be interpreted
/// using only the current platform's <see cref="Path"/> rules.
/// </summary>
public static class ArchivePath
{
    private static readonly char[] Separators = ['/', '\\'];

    public static string GetFileName(string path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;
        var index = path.LastIndexOfAny(Separators);
        return index < 0 ? path : path[(index + 1)..];
    }

    public static string GetFileNameWithoutExtension(string path)
        => Path.GetFileNameWithoutExtension(GetFileName(path));

    public static string? GetDirectoryName(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var index = path.LastIndexOfAny(Separators);
        return index < 0 ? null : path[..index];
    }

    public static IEnumerable<string> Components(string path)
        => (path ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries);
}
