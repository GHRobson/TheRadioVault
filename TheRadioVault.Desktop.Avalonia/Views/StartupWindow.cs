using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TheRadioVault.Desktop.Avalonia.Platform;

namespace TheRadioVault.Desktop.Avalonia.Views;

public sealed partial class StartupWindow : Window
{
    public StartupWindow() => InitializeComponent();

    public void SetStatus(string status, string? detail = null, double? progress = null)
    {
        StartupStatusText.Text = status;
        if (!string.IsNullOrWhiteSpace(detail)) StartupDetailText.Text = detail;
        if (progress.HasValue) StartupProgressBar.Value = Math.Clamp(progress.Value, 0d, 1d);
    }

    public void ConfigureSession(
        string serverDisplayName,
        bool remoteServer,
        long cacheSizeBytes,
        DateTimeOffset? lastCacheSyncAt)
    {
        ConnectionModeText.Text = remoteServer ? "REMOTE SERVER" : "THIS COMPUTER";
        StartupSourceText.Text = string.IsNullOrWhiteSpace(serverDisplayName)
            ? "Radio Vault Server"
            : serverDisplayName.Trim();
        if (cacheSizeBytes <= 0)
        {
            StartupCacheText.Text = remoteServer
                ? "Creating an encrypted saved workspace"
                : "Using the server on this computer";
            return;
        }

        var size = cacheSizeBytes >= 1024 * 1024
            ? $"{cacheSizeBytes / (1024d * 1024d):0.#} MB"
            : $"{Math.Max(1, cacheSizeBytes / 1024d):0} KB";
        var age = lastCacheSyncAt.HasValue
            ? $" · last checked {DescribeAge(DateTimeOffset.UtcNow - lastCacheSyncAt.Value)}"
            : string.Empty;
        StartupCacheText.Text = $"Encrypted saved workspace · {size}{age}";
    }

    public void ShowFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Title = "Radio Vault startup error";
        Width = 780;
        Height = 590;
        CanResize = true;
        LoadingPanel.IsVisible = false;
        FailurePanel.IsVisible = true;
        FailureSummaryText.Text = exception.Message;
        FailureDetailsText.Text = exception.ToString();
    }

    private void Window_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e);
    }

    private void OpenLogLocation_OnClick(object? sender, RoutedEventArgs e) => OpenLogLocation();
    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();

    private static void OpenLogLocation()
    {
        try
        {
            var path = StartupFailureReporter.LogPath;
            if (OperatingSystem.IsWindows())
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }

            var directory = Path.GetDirectoryName(path) ?? ".";
            var executable = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            Process.Start(new ProcessStartInfo(executable, $"\"{directory}\"") { UseShellExecute = false });
        }
        catch
        {
        }
    }

    private static string DescribeAge(TimeSpan age)
    {
        if (age < TimeSpan.Zero) age = TimeSpan.Zero;
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return $"{Math.Max(1, (int)age.TotalMinutes)}m ago";
        if (age < TimeSpan.FromDays(1)) return $"{Math.Max(1, (int)age.TotalHours)}h ago";
        return $"{Math.Max(1, (int)age.TotalDays)}d ago";
    }
}
