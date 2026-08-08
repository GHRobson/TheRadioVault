using Avalonia;
using TheRadioVault.Server.Services;

namespace TheRadioVault.Server;

internal static class Program
{
    internal static ServerInstanceCoordinator? InstanceCoordinator { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        InstanceCoordinator = ServerInstanceCoordinator.Acquire();
        if (!InstanceCoordinator.IsPrimary)
        {
            ServerInstanceCoordinator.SignalPrimaryAsync().GetAwaiter().GetResult();
            InstanceCoordinator.Dispose();
            InstanceCoordinator = null;
            return 0;
        }

        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            InstanceCoordinator?.Dispose();
            InstanceCoordinator = null;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
