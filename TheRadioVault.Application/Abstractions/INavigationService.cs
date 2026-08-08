using TheRadioVault.Application.Models;

namespace TheRadioVault.Application.Abstractions;

public interface INavigationService
{
    string? CurrentRoute { get; }
    Task NavigateAsync(NavigationRequest request, CancellationToken cancellationToken = default);
    Task<bool> TryGoBackAsync(CancellationToken cancellationToken = default);
}
