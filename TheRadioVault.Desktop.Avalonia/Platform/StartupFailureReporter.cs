using System.Runtime.InteropServices;
using System.Text;

namespace TheRadioVault.Desktop.Avalonia.Platform;

public static class StartupFailureReporter
{
    private const uint MbOk = 0x00000000;
    private const uint MbIconError = 0x00000010;
    private static readonly object Gate = new();
    private static int _handlersInstalled;

    public static string LogPath => AvaloniaAppPaths.StartupFailureLogPath;

    public static void InstallGlobalHandlers()
    {
        if (Interlocked.Exchange(ref _handlersInstalled, 1) != 0) return;

        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            var exception = eventArgs.ExceptionObject as Exception
                ?? new InvalidOperationException($"Unhandled non-Exception object: {eventArgs.ExceptionObject}");
            Report("AppDomain unhandled exception", exception, showNativeDialog: false);
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Report("Unobserved task exception", eventArgs.Exception, showNativeDialog: false);
            eventArgs.SetObserved();
        };
    }

    public static void Checkpoint(string message) => Write("Checkpoint", message);

    public static void Report(string area, Exception exception, bool showNativeDialog)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var details = BuildDetails(area, exception);
        WriteRaw(details);

        if (showNativeDialog)
            ShowNativeFailure(area, exception.Message);
    }

    private static string BuildDetails(string area, Exception exception)
    {
        var builder = new StringBuilder();
        builder.AppendLine(new string('=', 84));
        builder.AppendLine($"[{DateTimeOffset.Now:O}] {area}");
        builder.AppendLine($"Process: {Environment.ProcessPath ?? "unknown"}");
        builder.AppendLine($"Working directory: {Environment.CurrentDirectory}");
        builder.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        builder.AppendLine($"Framework: {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine(exception.ToString());
        return builder.ToString();
    }

    private static void Write(string area, string message) =>
        WriteRaw($"[{DateTimeOffset.Now:O}] [{area}] {message}{Environment.NewLine}");

    private static void WriteRaw(string text)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                File.AppendAllText(LogPath, text, Encoding.UTF8);
            }
        }
        catch
        {
            // Startup diagnostics must never replace the original failure.
        }
    }

    private static void ShowNativeFailure(string area, string message)
    {
        string logPath;
        try { logPath = LogPath; }
        catch { logPath = "the Radio Vault application-data folder"; }
        var body = $"Radio Vault could not start.\n\n{area}: {message}\n\nA detailed log was written to:\n{logPath}";
        try
        {
            if (OperatingSystem.IsWindows())
            {
                _ = MessageBoxW(IntPtr.Zero, body, "Radio Vault startup error", MbOk | MbIconError);
                return;
            }
        }
        catch
        {
        }

        try { Console.Error.WriteLine(body); }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
