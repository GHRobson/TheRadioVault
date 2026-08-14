using System.Reflection;

namespace TheRadioVault.Services;

public static class AppVersionService
{
    private static readonly Assembly ApplicationAssembly =
        Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();

    private static readonly string InformationalVersion =
        ApplicationAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? ApplicationAssembly.GetName().Version?.ToString()
        ?? "unknown";

    public static string Version
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(InformationalVersion))
                return InformationalVersion.Split('+')[0];

            var version = ApplicationAssembly.GetName().Version;
            return version is null
                ? "unknown"
                : $"{version.Major}.{version.Minor}.{version.Build}";
        }
    }

    public static string BuildIdentity
    {
        get
        {
            var separator = InformationalVersion.IndexOf('+');
            return separator >= 0 && separator < InformationalVersion.Length - 1
                ? InformationalVersion[(separator + 1)..]
                : "unknown";
        }
    }

    public static string ShortBuildIdentity
    {
        get
        {
            if (BuildIdentity is "local" or "unknown") return BuildIdentity;
            var separator = BuildIdentity.IndexOf('.');
            var commit = separator < 0 ? BuildIdentity : BuildIdentity[..separator];
            var suffix = separator < 0 ? string.Empty : BuildIdentity[separator..];
            return commit[..Math.Min(12, commit.Length)] + suffix;
        }
    }

    public static string DisplayVersion => $"v{Version} · {ShortBuildIdentity}";
}
