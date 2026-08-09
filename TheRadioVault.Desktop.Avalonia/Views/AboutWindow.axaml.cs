using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TheRadioVault.Desktop.Avalonia.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var assembly = Assembly.GetEntryAssembly() ?? typeof(AboutWindow).Assembly;
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? assembly.GetName().Version?.ToString()
                      ?? "Unknown";
        var metadataSeparator = version.IndexOf('+');
        if (metadataSeparator >= 0) version = version[..metadataSeparator];
        VersionText.Text = $"Version {version}";
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
