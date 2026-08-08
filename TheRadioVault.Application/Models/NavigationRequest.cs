namespace TheRadioVault.Application.Models;

public sealed record NavigationRequest(
    string Route,
    IReadOnlyDictionary<string, object?>? Parameters = null)
{
    public static NavigationRequest To(string route) => new(route);
}

public sealed record WindowRequest(
    string WindowKey,
    object? Context = null,
    bool Modal = false);
