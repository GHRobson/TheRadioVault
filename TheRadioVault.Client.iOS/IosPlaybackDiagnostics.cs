namespace TheRadioVault.Client.iOS;

internal static class IosPlaybackDiagnostics
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "RadioVault-playback-diagnostic.log");

    public static void Reset()
    {
        lock (Gate)
        {
            try
            {
                File.WriteAllText(
                    LogPath,
                    $"{DateTimeOffset.UtcNow:O} [RadioVault iOS diagnostic] App launched{Environment.NewLine}");
            }
            catch { }
        }
    }

    public static void Write(string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {message}";
        Console.Error.WriteLine(line);
        lock (Gate)
        {
            try { File.AppendAllText(LogPath, line + Environment.NewLine); }
            catch { }
        }
    }
}
