using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using TheRadioVault.Desktop.Avalonia.Composition;
using TheRadioVault.Desktop.Avalonia.Platform;
using TheRadioVault.Desktop.Avalonia.Views;
using TheRadioVault.Presentation.ViewModels;

namespace TheRadioVault.Desktop.Avalonia;

public partial class App : global::Avalonia.Application
{
    private AvaloniaApplicationHost? _host;
    private AboutWindow? _aboutWindow;
    private int _startupStarted;

    public override void Initialize()
    {
        try
        {
            StartupFailureReporter.Checkpoint("Loading App.axaml.");
            AvaloniaXamlLoader.Load(this);
            StartupFailureReporter.Checkpoint("App.axaml loaded.");
        }
        catch (Exception exception)
        {
            StartupFailureReporter.Report("Application XAML initialization", exception, showNativeDialog: true);
            throw;
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
        {
            StartupFailureReporter.Report("Avalonia UI thread", eventArgs.Exception, showNativeDialog: false);
            eventArgs.Handled = true;
        };

        try
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                StartupFailureReporter.Checkpoint("Classic desktop lifetime acquired.");
                _ = new AvaloniaThemeService(AvaloniaAppPaths.ThemePreferencePath);
                var startupWindow = new StartupWindow();
                var windowProvider = new AvaloniaWindowProvider { MainWindow = startupWindow };
                desktop.MainWindow = startupWindow;
                startupWindow.Opened += async (_, _) =>
                    await InitializeDesktopAsync(desktop, startupWindow, windowProvider).ConfigureAwait(true);
                desktop.Exit += (_, _) => _host?.Dispose();
            }

            base.OnFrameworkInitializationCompleted();
        }
        catch (Exception exception)
        {
            StartupFailureReporter.Report("Desktop lifetime initialization", exception, showNativeDialog: true);
            throw;
        }
    }

    private async Task InitializeDesktopAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        StartupWindow startupWindow,
        AvaloniaWindowProvider windowProvider)
    {
        if (Interlocked.Exchange(ref _startupStarted, 1) != 0) return;
        var minimumSplashTime = Task.Delay(TimeSpan.FromSeconds(3));

        try
        {
            startupWindow.SetStatus(
                "Choosing your server",
                "Reading the saved connection and startup preference.",
                0.12d);
            StartupFailureReporter.Checkpoint("Beginning application host creation.");

            var options = AvaloniaStartupOptions.Parse(Environment.GetCommandLineArgs().Skip(1));
            var host = await Task.Run(() => AvaloniaApplicationHost.Create(options, windowProvider, desktop))
                .ConfigureAwait(true);
            _host = host;
            StartupFailureReporter.Checkpoint("Application host created.");

            startupWindow.ConfigureSession(
                host.ServerDisplayName,
                host.IsRemoteSession,
                host.StartupCacheSizeBytes,
                host.LastCacheSyncAt);
            startupWindow.SetStatus(
                host.StartupCacheSizeBytes > 0 && host.IsRemoteSession
                    ? "Restoring your workspace"
                    : host.IsRemoteSession
                        ? "Loading your workspace"
                        : "Loading from this computer",
                host.StartupCacheSizeBytes > 0 && host.IsRemoteSession
                    ? $"Using encrypted saved views where available. Anything missing will be requested from {host.ServerDisplayName}."
                    : host.IsRemoteSession
                        ? $"This device has no saved workspace yet. Views will be saved securely as they load from {host.ServerDisplayName}."
                        : $"Requesting the Dashboard and show navigation from {host.ServerDisplayName}.",
                0.42d);
            await host.InitializeStartupAsync().ConfigureAwait(true);
            StartupFailureReporter.Checkpoint("Cache-first workspace initialization completed.");

            startupWindow.SetStatus(
                "Opening Radio Vault",
                "The desktop will check for newer server changes after it opens.",
                0.94d);
            await minimumSplashTime.ConfigureAwait(true);
            var mainWindow = new MainWindow { DataContext = host.MainWindow };
            windowProvider.MainWindow = mainWindow;
            desktop.MainWindow = mainWindow;
            mainWindow.Show();
            startupWindow.Close();
            StartupFailureReporter.Checkpoint("Main window shown.");
            _ = RefreshStartupCacheAfterLaunchAsync(host);
        }
        catch (Exception exception)
        {
            StartupFailureReporter.Report("Application host creation", exception, showNativeDialog: false);
            startupWindow.ShowFailure(exception);
        }
    }

    private static async Task RefreshStartupCacheAfterLaunchAsync(AvaloniaApplicationHost host)
    {
        try
        {
            await host.RefreshStartupCacheAsync().ConfigureAwait(true);
            StartupFailureReporter.Checkpoint("Incremental native-client cache refresh completed.");
        }
        catch (Exception exception)
        {
            // The encrypted saved workspace is already usable. Connection state
            // monitoring will retry, so a refresh failure must not close the UI.
            StartupFailureReporter.Report("Incremental startup cache refresh", exception, showNativeDialog: false);
        }
    }

    private void AboutMenuItem_OnClick(object? sender, EventArgs args)
    {
        if (_aboutWindow is not null)
        {
            _aboutWindow.Activate();
            return;
        }

        var aboutWindow = new AboutWindow();
        _aboutWindow = aboutWindow;
        aboutWindow.Closed += (_, _) => _aboutWindow = null;
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: Window owner })
            _ = aboutWindow.ShowDialog(owner);
        else
            aboutWindow.Show();
    }

    private void SettingsMenuItem_OnClick(object? sender, EventArgs args)
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            {
                MainWindow: MainWindow { DataContext: MainWindowViewModel viewModel } mainWindow
            })
        {
            mainWindow.Activate();
            _ = viewModel.NavigateToAsync("tools");
        }
    }
}
