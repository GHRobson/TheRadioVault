namespace TheRadioVault.Desktop.Avalonia.Composition;

public sealed record AvaloniaStartupOptions(
    string? DatabasePath,
    bool ForceLocalLibrary = false,
    bool ForceRemoteLibrary = false)
{
    public static AvaloniaStartupOptions Parse(IEnumerable<string> arguments)
    {
        var args = arguments?.ToArray() ?? Array.Empty<string>();
        string? database = null;
        var local = false;
        var remote = false;
        for (var index = 0; index < args.Length; index++)
        {
            var value = args[index];
            if (value.StartsWith("--database=", StringComparison.OrdinalIgnoreCase)) database = value[11..].Trim('"');
            else if (string.Equals(value, "--database", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length) database = args[++index].Trim('"');
            else if (string.Equals(value, "--local", StringComparison.OrdinalIgnoreCase)) local = true;
            else if (string.Equals(value, "--remote", StringComparison.OrdinalIgnoreCase)) remote = true;
        }
        if (local && remote)
            throw new InvalidOperationException("Choose either --local or --remote, not both.");
        return new AvaloniaStartupOptions(database, ForceLocalLibrary: local, ForceRemoteLibrary: remote);
    }
}
