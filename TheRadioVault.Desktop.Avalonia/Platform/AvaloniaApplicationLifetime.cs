using AvaloniaDesktopLifetime = Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime;
using RadioVaultApplicationLifetime = TheRadioVault.Application.Abstractions.IApplicationLifetime;

namespace TheRadioVault.Desktop.Avalonia.Platform;

public sealed class AvaloniaApplicationLifetime : RadioVaultApplicationLifetime
{
    private readonly AvaloniaDesktopLifetime _lifetime;
    private int _shutdownRequested;

    public AvaloniaApplicationLifetime(AvaloniaDesktopLifetime lifetime) => _lifetime = lifetime;
    public bool IsShutdownRequested => Volatile.Read(ref _shutdownRequested) != 0;

    public void RequestShutdown(int exitCode = 0)
    {
        Interlocked.Exchange(ref _shutdownRequested, 1);
        _lifetime.Shutdown(exitCode);
    }
}
