using Avalonia.Styling;
using TheRadioVault.Application.Abstractions;

namespace TheRadioVault.Desktop.Avalonia.Platform;

public sealed class AvaloniaSystemAppearanceService : ISystemAppearanceService
{
    public bool PrefersLightTheme() =>
        global::Avalonia.Application.Current?.ActualThemeVariant == ThemeVariant.Light;
}
