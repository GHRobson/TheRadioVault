namespace TheRadioVault.Desktop.Avalonia.Platform;

public static class AvaloniaDiagnosticLog
{
    private static readonly object Gate = new();

    public static void Write(string area, string message)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(
                    AvaloniaAppPaths.DiagnosticLogPath,
                    $"[{DateTimeOffset.Now:O}] [{area}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Diagnostics must never stop application startup.
        }
    }
}
