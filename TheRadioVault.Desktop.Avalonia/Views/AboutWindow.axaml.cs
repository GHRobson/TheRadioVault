using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using TheRadioVault.Services;

namespace TheRadioVault.Desktop.Avalonia.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppVersionService.Version} · build {AppVersionService.ShortBuildIdentity}";
        var platform = OperatingSystem.IsMacOS() ? "macOS"
            : OperatingSystem.IsLinux() ? "Linux"
            : OperatingSystem.IsWindows() ? "Windows"
            : RuntimeInformation.OSDescription;
        PlatformText.Text = $"{platform} client • {RuntimeInformation.ProcessArchitecture}";
    }

    private static void ProjectWebsite_OnClick(object? sender, RoutedEventArgs e)
        => Process.Start(new ProcessStartInfo("https://github.com/GHRobson/TheRadioVault")
        {
            UseShellExecute = true
        });

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();
}
