using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace TheRadioVault.Server.Views;

public partial class ServerSettingsWindow : Window
{
    public ServerSettingsWindow() => InitializeComponent();

    public bool ExitRequested { get; set; }

    public void ShowSettings()
    {
        ShowInTaskbar = true;
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!ExitRequested && Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime)
        {
            e.Cancel = true;
            ShowInTaskbar = false;
            Hide();
            return;
        }
        base.OnClosing(e);
    }
}
