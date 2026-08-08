using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using TheRadioVault.Server.Services;
using TheRadioVault.Server.ViewModels;
using TheRadioVault.Server.Views;
using TheRadioVault.Services;

namespace TheRadioVault.Server;

public partial class App : Avalonia.Application
{
    private RadioVaultServerRuntime? _runtime;
    private ServerSettingsWindow? _settingsWindow;
    private ServerSettingsViewModel? _viewModel;
    private TrayIcon? _trayIcon;
    private bool _exiting;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _settingsWindow = new ServerSettingsWindow();
            try
            {
                var databasePath = ReadDatabasePath(desktop.Args) ?? AppPaths.DatabasePath;
                _runtime = new RadioVaultServerRuntime(databasePath);
                _viewModel = new ServerSettingsViewModel(
                    _runtime,
                    new ServerFolderSelectionService(_settingsWindow),
                    new ServerShowSelectionService(_settingsWindow),
                    new ServerKnowledgeFileService(_settingsWindow),
                    new ServerClipboardService(_settingsWindow));
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write("Server", "Dedicated server startup failed.", exception);
                _viewModel = new ServerSettingsViewModel(exception);
            }

            _settingsWindow.DataContext = _viewModel;
            desktop.MainWindow = _settingsWindow;
            CreateTrayIcon(desktop);
            Program.InstanceCoordinator?.StartListening(() => Dispatcher.UIThread.Post(ShowSettings));

            if (HasArgument(desktop.Args, "--background"))
                _settingsWindow.Opened += (_, _) => Dispatcher.UIThread.Post(() =>
                {
                    _settingsWindow.ShowInTaskbar = false;
                    _settingsWindow.Hide();
                });

            desktop.Exit += (_, _) =>
            {
                _trayIcon?.Dispose();
                _trayIcon = null;
                _viewModel?.Dispose();
                _runtime?.Dispose();
            };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void CreateTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var menu = new NativeMenu();
        var show = new NativeMenuItem("Open server settings");
        show.Click += (_, _) => ShowSettings();
        var openAnywhere = new NativeMenuItem("Open Radio Vault Web");
        openAnywhere.Click += (_, _) => _viewModel?.OpenAnywhereCommand.Execute(null);
        var exit = new NativeMenuItem("Exit Radio Vault Server");
        exit.Click += (_, _) => ExitServer(desktop);
        menu.Add(show);
        menu.Add(openAnywhere);
        menu.Add(new NativeMenuItemSeparator());
        menu.Add(exit);

        using var iconStream = AssetLoader.Open(new Uri("avares://RadioVault.Server/Assets/RadioVault.Server.ico"));
        _trayIcon = new TrayIcon
        {
            Icon = new WindowIcon(iconStream),
            ToolTipText = "Radio Vault Server",
            Menu = menu,
            IsVisible = true
        };
        _trayIcon.Clicked += (_, _) => ShowSettings();
    }

    private void ShowSettings()
    {
        if (_exiting || _settingsWindow is null) return;
        _settingsWindow.ShowSettings();
    }

    private void ExitServer(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (_exiting) return;
        _exiting = true;
        if (_settingsWindow is not null)
        {
            _settingsWindow.ExitRequested = true;
            _settingsWindow.Close();
        }
        desktop.Shutdown();
    }

    private static string? ReadDatabasePath(IEnumerable<string>? arguments)
    {
        var args = arguments?.ToArray() ?? Array.Empty<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var value = args[index];
            if (value.StartsWith("--database=", StringComparison.OrdinalIgnoreCase)) return value[11..].Trim('"');
            if (string.Equals(value, "--database", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Length)
                return args[index + 1].Trim('"');
        }
        return null;
    }

    private static bool HasArgument(IEnumerable<string>? arguments, string expected)
        => arguments?.Any(value => string.Equals(value, expected, StringComparison.OrdinalIgnoreCase)) == true;
}
