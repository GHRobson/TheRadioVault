using System.Security;
using System.Text;

namespace TheRadioVault.Server.Services;

/// <summary>
/// Registers the packaged server for the current user's desktop session without
/// leaking platform-specific startup mechanisms into the server view model.
/// </summary>
public sealed class ServerStartupRegistrationService
{
    private const string MacLabel = "com.theradiovault.server";
    private readonly WindowsStartupRegistrationService? _windows;

    public ServerStartupRegistrationService(string? databasePath = null)
    {
        DatabasePath = string.IsNullOrWhiteSpace(databasePath) ? string.Empty : Path.GetFullPath(databasePath);
        ExecutablePath = ResolveExecutablePath();
        if (OperatingSystem.IsWindows()) _windows = new WindowsStartupRegistrationService(DatabasePath);
    }

    public string ExecutablePath { get; }
    public string DatabasePath { get; }
    public string SettingLabel => OperatingSystem.IsWindows() ? "Start with Windows"
        : OperatingSystem.IsMacOS() ? "Start with macOS"
        : OperatingSystem.IsLinux() ? "Start with Linux"
        : "Start automatically";

    public bool IsSupported
        => _windows?.IsSupported == true
           || ((OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
               && ExecutablePath.Length > 0
               && File.Exists(ExecutablePath));

    public bool IsRegistered
    {
        get
        {
            if (_windows is not null) return _windows.IsRegistered;
            if (!IsSupported || RegistrationPath.Length == 0) return false;
            try
            {
                return File.Exists(RegistrationPath)
                       && string.Equals(File.ReadAllText(RegistrationPath), BuildRegistrationFile(), StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }
    }

    public string StatusText => !IsSupported
        ? $"Automatic startup is available after installing the packaged {PlatformName} server."
        : IsRegistered
            ? $"Registered to start in the background when you sign in to {PlatformName}."
            : $"Not currently registered in {PlatformName} sign-in startup.";

    public string SetEnabled(bool enabled)
    {
        if (_windows is not null) return _windows.SetEnabled(enabled);
        if (!IsSupported || RegistrationPath.Length == 0)
            return $"Automatic startup is available after installing the packaged {PlatformName} server.";

        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(RegistrationPath)!);
            File.WriteAllText(RegistrationPath, BuildRegistrationFile(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return $"Radio Vault Server will start in the background when you sign in to {PlatformName}.";
        }

        if (File.Exists(RegistrationPath)) File.Delete(RegistrationPath);
        return $"Radio Vault Server was removed from {PlatformName} sign-in startup.";
    }

    public string BuildCommand()
    {
        var command = QuoteCommandArgument(ExecutablePath) + " --background";
        if (DatabasePath.Length > 0) command += " --database " + QuoteCommandArgument(DatabasePath);
        return command;
    }

    private string RegistrationPath
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (home.Length == 0) return string.Empty;
            if (OperatingSystem.IsMacOS()) return Path.Combine(home, "Library", "LaunchAgents", MacLabel + ".plist");
            if (OperatingSystem.IsLinux()) return Path.Combine(home, ".config", "autostart", "theradiovault-server.desktop");
            return string.Empty;
        }
    }

    private string PlatformName => OperatingSystem.IsMacOS() ? "macOS"
        : OperatingSystem.IsLinux() ? "Linux"
        : OperatingSystem.IsWindows() ? "Windows"
        : "this computer";

    private string BuildRegistrationFile()
        => OperatingSystem.IsMacOS() ? BuildMacLaunchAgent() : BuildLinuxDesktopEntry();

    private string BuildMacLaunchAgent()
    {
        var executable = SecurityElement.Escape(ExecutablePath) ?? string.Empty;
        var database = SecurityElement.Escape(DatabasePath) ?? string.Empty;
        var databaseArguments = DatabasePath.Length == 0
            ? string.Empty
            : $"\n      <string>--database</string>\n      <string>{database}</string>";
        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "https://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
  <dict>
    <key>Label</key>
    <string>{MacLabel}</string>
    <key>ProgramArguments</key>
    <array>
      <string>{executable}</string>
      <string>--background</string>{databaseArguments}
    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>ProcessType</key>
    <string>Interactive</string>
  </dict>
</plist>
""";
    }

    private string BuildLinuxDesktopEntry()
        => $"""
[Desktop Entry]
Type=Application
Name=Radio Vault Server
Comment=Start the private Radio Vault archive server
Exec={BuildCommand()}
Terminal=false
X-GNOME-Autostart-enabled=true

""";

    private static string QuoteCommandArgument(string value)
        => '"' + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + '"';

    private static string ResolveExecutablePath()
    {
        var processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath)
            && !string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(processPath);

        var assemblyPath = System.Reflection.Assembly.GetEntryAssembly()?.Location;
        if (string.IsNullOrWhiteSpace(assemblyPath)) return string.Empty;
        var appHost = OperatingSystem.IsWindows()
            ? Path.ChangeExtension(assemblyPath, ".exe")
            : Path.Combine(Path.GetDirectoryName(assemblyPath)!, Path.GetFileNameWithoutExtension(assemblyPath));
        return File.Exists(appHost) ? Path.GetFullPath(appHost) : string.Empty;
    }
}
