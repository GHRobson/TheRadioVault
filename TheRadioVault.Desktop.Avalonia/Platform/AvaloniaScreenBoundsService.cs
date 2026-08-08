using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;

namespace TheRadioVault.Desktop.Avalonia.Platform;

public sealed class AvaloniaScreenBoundsService : IScreenBoundsService
{
    private readonly AvaloniaWindowProvider _windows;
    public AvaloniaScreenBoundsService(AvaloniaWindowProvider windows) => _windows = windows;

    public bool IntersectsVirtualScreen(WindowBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return false;
        var screens = _windows.MainWindow?.Screens.All;
        if (screens is null || screens.Count == 0) return true;
        return screens.Any(screen =>
        {
            var scale = Math.Max(0.1, screen.Scaling);
            var area = screen.WorkingArea;
            var left = area.X / scale;
            var top = area.Y / scale;
            var right = area.Right / scale;
            var bottom = area.Bottom / scale;
            return bounds.Right > left && bounds.Left < right && bounds.Bottom > top && bounds.Top < bottom;
        });
    }
}
