using System.Text.Json;
using Avalonia.Styling;
using Avalonia.Threading;
using TheRadioVault.Application.Abstractions;

namespace TheRadioVault.Desktop.Avalonia.Platform;

public sealed class AvaloniaThemeService : IAppThemeService
{
    private sealed record Preference(string Mode);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _preferencePath;

    public AvaloniaThemeService(string preferencePath)
    {
        _preferencePath = preferencePath;
        CurrentMode = Load();
        ApplyCore(CurrentMode);
    }

    public AppThemeMode CurrentMode { get; private set; }
    public event EventHandler? ThemeChanged;

    public void Apply(AppThemeMode mode)
    {
        CurrentMode = mode;
        ApplyCore(mode);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_preferencePath) ?? ".");
            File.WriteAllText(_preferencePath, JsonSerializer.Serialize(new Preference(mode.ToString()), JsonOptions));
        }
        catch
        {
            // Theme changes should still apply when the preference file cannot be written.
        }
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private AppThemeMode Load()
    {
        try
        {
            if (!File.Exists(_preferencePath)) return AppThemeMode.System;
            var value = JsonSerializer.Deserialize<Preference>(File.ReadAllText(_preferencePath));
            return Enum.TryParse<AppThemeMode>(value?.Mode, true, out var mode) ? mode : AppThemeMode.System;
        }
        catch
        {
            return AppThemeMode.System;
        }
    }

    private static void ApplyCore(AppThemeMode mode)
    {
        var variant = mode switch
        {
            AppThemeMode.Light => ThemeVariant.Light,
            AppThemeMode.Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default
        };

        void ApplyVariant()
        {
            if (global::Avalonia.Application.Current is { } application)
                application.RequestedThemeVariant = variant;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyVariant();
            return;
        }

        Dispatcher.UIThread.Post(ApplyVariant);
    }
}
