namespace TheRadioVault.Application.Abstractions;

public enum AppThemeMode
{
    System,
    Light,
    Dark
}

/// <summary>
/// Applies and persists the user-facing desktop colour scheme without exposing
/// a UI toolkit to the presentation layer.
/// </summary>
public interface IAppThemeService
{
    AppThemeMode CurrentMode { get; }
    event EventHandler? ThemeChanged;
    void Apply(AppThemeMode mode);
}
