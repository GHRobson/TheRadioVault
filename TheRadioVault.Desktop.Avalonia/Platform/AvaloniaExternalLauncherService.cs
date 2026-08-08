using System.Diagnostics;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;

namespace TheRadioVault.Desktop.Avalonia.Platform;

public sealed class AvaloniaExternalLauncherService : IExternalLauncherService
{
    public Task LaunchAsync(ExternalLaunchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        switch (request.Kind)
        {
            case ExternalLaunchKind.RevealFile:
                Reveal(request.Target);
                break;
            case ExternalLaunchKind.LaunchExecutable:
                Process.Start(new ProcessStartInfo(request.Target, request.Arguments ?? string.Empty) { UseShellExecute = true });
                break;
            default:
                Process.Start(new ProcessStartInfo(request.Target) { UseShellExecute = true });
                break;
        }
        return Task.CompletedTask;
    }

    private static void Reveal(string path)
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        else if (OperatingSystem.IsMacOS())
            Process.Start(new ProcessStartInfo("open", $"-R \"{path}\"") { UseShellExecute = false });
        else
            Process.Start(new ProcessStartInfo("xdg-open", $"\"{Path.GetDirectoryName(path) ?? path}\"") { UseShellExecute = false });
    }
}
