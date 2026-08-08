using Avalonia.Input.Platform;
using TheRadioVault.Application.Abstractions;

namespace TheRadioVault.Desktop.Avalonia.Platform;

public sealed class AvaloniaClipboardService : IClipboardService
{
    private readonly AvaloniaWindowProvider _windows;
    public AvaloniaClipboardService(AvaloniaWindowProvider windows) => _windows = windows;

    public async Task<string?> GetTextAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clipboard = _windows.MainWindow?.Clipboard;
        return clipboard is null ? null : await clipboard.TryGetTextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var clipboard = _windows.MainWindow?.Clipboard
            ?? throw new InvalidOperationException("The main window clipboard is not available yet.");
        await clipboard.SetTextAsync(text ?? string.Empty).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
