namespace TheRadioVault.Application.Models;

public enum UserNotificationSeverity
{
    Information,
    Success,
    Warning,
    Error
}

public sealed record UserNotification(
    string Title,
    string Message,
    UserNotificationSeverity Severity = UserNotificationSeverity.Information);
