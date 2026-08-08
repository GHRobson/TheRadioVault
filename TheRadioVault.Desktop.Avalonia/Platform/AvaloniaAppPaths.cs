namespace TheRadioVault.Desktop.Avalonia.Platform;

public static class AvaloniaAppPaths
{
    public static string DataDirectory
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TheRadioVault");
            Directory.CreateDirectory(path);
            return path;
        }
    }

    public static string DatabasePath => Path.Combine(DataDirectory, "radio_vault.db");
    public static string DiagnosticLogPath => Path.Combine(DataDirectory, "avalonia-alpha.log");
    public static string StartupFailureLogPath => Path.Combine(DataDirectory, "avalonia-startup-failure.log");
    public static string ThemePreferencePath => Path.Combine(DataDirectory, "theme-preference.json");
}
