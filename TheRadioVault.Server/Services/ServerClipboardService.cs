using Avalonia.Controls;
using Avalonia.Input.Platform;

namespace TheRadioVault.Server.Services;

public sealed class ServerClipboardService
{
    private readonly Window _owner;

    public ServerClipboardService(Window owner)
        => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public async Task SetTextAsync(string text, CancellationToken cancellationToken = default)
    {
        var clipboard = _owner.Clipboard
            ?? throw new InvalidOperationException("The server administration clipboard is not available yet.");
        await clipboard.SetTextAsync(text ?? string.Empty).WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
