using TheRadioVault.Application.Models;

namespace TheRadioVault.Application.Abstractions;

public interface IWindowCoordinator
{
    Task<object?> OpenAsync(WindowRequest request, CancellationToken cancellationToken = default);
    Task CloseAsync(string windowKey, CancellationToken cancellationToken = default);
}
