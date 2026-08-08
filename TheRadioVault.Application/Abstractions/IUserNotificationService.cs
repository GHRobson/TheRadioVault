using TheRadioVault.Application.Models;

namespace TheRadioVault.Application.Abstractions;

public interface IUserNotificationService
{
    Task ShowAsync(UserNotification notification, CancellationToken cancellationToken = default);
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken = default);
}
