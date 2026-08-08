using Avalonia.Controls;
using Avalonia.Layout;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;

namespace TheRadioVault.Desktop.Avalonia.Platform;

public sealed class AvaloniaUserNotificationService : IUserNotificationService
{
    private readonly AvaloniaWindowProvider _windows;
    public AvaloniaUserNotificationService(AvaloniaWindowProvider windows) => _windows = windows;

    public async Task ShowAsync(UserNotification notification, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        await ShowDialogAsync(notification.Title, notification.Message, confirm: false, cancellationToken).ConfigureAwait(false);
    }

    public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default) =>
        ShowDialogAsync(title, message, confirm: true, cancellationToken);

    private async Task<bool> ShowDialogAsync(string title, string message, bool confirm, CancellationToken cancellationToken)
    {
        var owner = _windows.MainWindow ?? throw new InvalidOperationException("The main window is not available yet.");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var ok = new Button { Content = confirm ? "Continue" : "OK", MinWidth = 96 };
        var cancel = new Button { Content = "Cancel", MinWidth = 96, IsVisible = confirm };
        ok.Click += (_, _) => { completion.TrySetResult(true); dialog.Close(); };
        cancel.Click += (_, _) => { completion.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => completion.TrySetResult(!confirm);
        dialog.Content = new StackPanel
        {
            Margin = new global::Avalonia.Thickness(22),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 10,
                    Children = { cancel, ok }
                }
            }
        };
        _ = dialog.ShowDialog(owner);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
}
