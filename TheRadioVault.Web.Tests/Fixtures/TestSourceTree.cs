namespace TheRadioVault.Web.Tests.Fixtures;

internal static class TestSourceTree
{
    public static string ReadWebServerSourceBundle()
    {
        var root = FindSourceRoot();
        var services = Path.Combine(root, "TheRadioVault.Web", "Services");
        var assets = Path.Combine(root, "TheRadioVault.Web", "Assets");
        var sources = Directory.GetFiles(services, "LocalWebServer*.cs")
            .OrderBy(path => path, StringComparer.Ordinal)
            .Concat(
            [
                Path.Combine(assets, "web-client.html"),
                Path.Combine(assets, "service-worker.js"),
                Path.Combine(assets, "secure-setup.html")
            ]);
        return string.Join(Environment.NewLine, sources.Select(File.ReadAllText));
    }

    private static string FindSourceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TheRadioVault.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate the Radio Vault source root from the Web test output directory.");
    }
}
