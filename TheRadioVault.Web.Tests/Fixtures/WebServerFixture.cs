using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;
using TheRadioVault.Web.Services;

namespace TheRadioVault.Web.Tests.Fixtures;

internal static class WebServerFixture
{
    public static void WithWebServer(Func<int, string, Task> test)
        => WithCustomWebServer(new TestWebArchiveProvider(), test);

    public static void WithCustomWebServer(
        IWebArchiveProvider archive,
        Func<int, string, Task> test,
        Action<string>? log = null)
    {
        var port = GetFreeTcpPort();
        var token = "test-token-" + Guid.NewGuid().ToString("N");
        using var server = new LocalWebServer(archive, new WebServerOptions
        {
            AppVersion = "test-web-version",
            ServerInstanceId = "11111111-2222-3333-4444-555555555555",
            ServerDisplayName = "Test Radio Vault",
            DatabaseSchemaVersion = 47,
            CapabilityGeneration = 3,
            Port = port,
            AccessToken = token,
            LoopbackOnly = true
        }, log);
        server.Start();
        try
        {
            test(port, token).GetAwaiter().GetResult();
        }
        finally
        {
            server.Stop();
        }
    }

    public static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public static int FindFreePort() => GetFreeTcpPort();
}
