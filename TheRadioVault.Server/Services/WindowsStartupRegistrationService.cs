using System.Reflection;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace TheRadioVault.Server.Services;

public sealed class WindowsStartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RadioVaultServer";

    public WindowsStartupRegistrationService(string? databasePath = null)
    {
        DatabasePath = string.IsNullOrWhiteSpace(databasePath) ? string.Empty : Path.GetFullPath(databasePath);
        ExecutablePath = ResolveExecutablePath();
    }

    public string ExecutablePath { get; }
    public string DatabasePath { get; }
    [SupportedOSPlatformGuard("windows")]
    public bool IsSupported => OperatingSystem.IsWindows() && ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    public bool IsRegistered
    {
        get
        {
            if (!IsSupported) return false;
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return string.Equals(key?.GetValue(ValueName) as string, BuildCommand(), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public string SetEnabled(bool enabled)
    {
        if (!IsSupported)
            return "Start-with-Windows registration is available in the packaged Windows server application.";

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("Windows did not allow access to the current-user startup list.");
        if (enabled)
        {
            key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
            return "Radio Vault Server will start in the background when you sign in to Windows.";
        }

        key.DeleteValue(ValueName, throwOnMissingValue: false);
        return "Radio Vault Server was removed from Windows sign-in startup.";
    }

    public string BuildCommand()
    {
        var command = $"\"{ExecutablePath}\" --background";
        if (!string.IsNullOrWhiteSpace(DatabasePath)) command += $" --database \"{DatabasePath}\"";
        return command;
    }

    private static string ResolveExecutablePath()
    {
        var assemblyPath = Assembly.GetEntryAssembly()?.Location;
        if (!string.IsNullOrWhiteSpace(assemblyPath))
        {
            var appHost = Path.ChangeExtension(assemblyPath, ".exe");
            if (File.Exists(appHost)) return Path.GetFullPath(appHost);
        }

        return string.IsNullOrWhiteSpace(Environment.ProcessPath)
            ? string.Empty
            : Path.GetFullPath(Environment.ProcessPath);
    }
}
