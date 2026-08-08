using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;

namespace TheRadioVault.Presentation.Navigation;

public sealed class ShellNavigationService : INavigationService
{
    private readonly Stack<string> _history = new();
    public string? CurrentRoute { get; private set; }
    public event EventHandler<NavigationRequest>? Navigated;

    public Task NavigateAsync(NavigationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(CurrentRoute) && !string.Equals(CurrentRoute, request.Route, StringComparison.Ordinal))
            _history.Push(CurrentRoute);
        CurrentRoute = request.Route;
        Navigated?.Invoke(this, request);
        return Task.CompletedTask;
    }

    public Task<bool> TryGoBackAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_history.Count == 0) return Task.FromResult(false);
        CurrentRoute = _history.Pop();
        Navigated?.Invoke(this, NavigationRequest.To(CurrentRoute));
        return Task.FromResult(true);
    }
}
