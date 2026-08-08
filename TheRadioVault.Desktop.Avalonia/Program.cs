using Avalonia;
using TheRadioVault.Desktop.Avalonia.Platform;

namespace TheRadioVault.Desktop.Avalonia;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        StartupFailureReporter.InstallGlobalHandlers();
        StartupFailureReporter.Checkpoint("Process entry reached.");
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            StartupFailureReporter.Report("Avalonia framework startup", exception, showNativeDialog: true);
            return 1;
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
