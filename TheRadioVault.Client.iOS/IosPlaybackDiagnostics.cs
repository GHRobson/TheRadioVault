using TheRadioVault.Client.Mobile.Diagnostics;

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

    public static string CreateExportFile(string report)
    {
        var exportPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            $"RadioVault-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt");
        lock (Gate)
        {
            var playbackLog = ReadLogUnsafe();
            File.WriteAllText(
                exportPath,
                MobileDiagnosticRedactor.Redact(report).TrimEnd() + Environment.NewLine + Environment.NewLine +
                "PLAYBACK LOG" + Environment.NewLine +
                "============" + Environment.NewLine +
                MobileDiagnosticRedactor.Redact(playbackLog));
        }
        Write("[RadioVault iOS diagnostic] Diagnostic report exported");
        return exportPath;
    }

    private static string ReadLogUnsafe()
    {
        try
        {
            return File.Exists(LogPath)
                ? File.ReadAllText(LogPath)
                : "No playback log is available for this launch.";
        }
        catch (Exception exception)
        {
            return $"The playback log could not be read: {exception.Message}";
        }
    }
}
