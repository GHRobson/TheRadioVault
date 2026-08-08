using System.Text;

namespace TheRadioVault.Services;

public static class DiagnosticLog
{
    private static readonly object Sync = new();
    private static string LogPath => Path.Combine(AppPaths.DataDirectory, "radio-vault.log");

    public static void Write(string area, string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var line = $"{DateTimeOffset.Now:O} [{area}] {message}";
            if (exception is not null) line += $" | {exception.GetType().Name}: {exception.Message}";
            lock (Sync)
            {
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
                TrimIfNeeded();
            }
        }
        catch { }
    }

    private static void TrimIfNeeded()
    {
        var file = new FileInfo(LogPath);
        if (!file.Exists || file.Length < 2_000_000) return;
        var lines = File.ReadAllLines(LogPath);
        File.WriteAllLines(LogPath, lines.Skip(lines.Length / 2), Encoding.UTF8);
    }
}
