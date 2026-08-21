using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Services;
using static TheRadioVault.Web.Tests.TestAssert;

namespace TheRadioVault.Web.Tests;

internal static class WebRequestLifecycleResolverTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases { get; } =
    [
        ("Request lifecycle protects pairing before authorization", PairingPrecedesAuthorization),
        ("Request lifecycle rejects unsupported methods and malformed targets", InvalidRequestsAreRejected),
        ("Request lifecycle keeps setup resources on authenticated HTTP", SetupResourcesPrecedeRedirect),
        ("Request lifecycle redirects other authenticated HTTP traffic", HttpTrafficRedirectsToHttps),
        ("Request lifecycle classifies secure shell assets", SecureShellAssetsAreClassified),
        ("Request lifecycle sends remaining requests to API dispatch", RemainingRequestsReachApiDispatch)
    ];

    private static void PairingPrecedesAuthorization()
    {
        var decision = Resolve("POST", WebApiRoutes.FederationPair, secure: true,
            secureAccessEnabled: true, authorized: false);
        Equal(WebRequestLifecycleKind.Pairing, decision.Kind);
        True(decision.Context?.IsPost == true);
    }

    private static void InvalidRequestsAreRejected()
    {
        Equal(WebRequestLifecycleKind.InvalidMethod,
            Resolve("DELETE", "/", secure: false, secureAccessEnabled: false, authorized: true).Kind);
        Equal(WebRequestLifecycleKind.MalformedTarget,
            Resolve("GET", "not-an-origin-target", secure: false, secureAccessEnabled: false, authorized: true).Kind);
    }

    private static void SetupResourcesPrecedeRedirect()
    {
        Equal(WebRequestLifecycleKind.SecureSetup,
            Resolve("GET", "/secure-setup?token=test", false, true, true).Kind);
        Equal(WebRequestLifecycleKind.SecureProfile,
            Resolve("HEAD", "/secure-profile.mobileconfig?token=test", false, true, true).Kind);
        Equal(WebRequestLifecycleKind.SecureRootCertificate,
            Resolve("GET", "/secure-root.cer?token=test", false, true, true).Kind);
    }

    private static void HttpTrafficRedirectsToHttps()
    {
        Equal(WebRequestLifecycleKind.Unauthorized,
            Resolve("GET", "/?token=wrong", false, true, false).Kind);
        Equal(WebRequestLifecycleKind.RedirectToSecure,
            Resolve("GET", "/?token=right", false, true, true).Kind);
        Equal(WebRequestLifecycleKind.RedirectToSecure,
            Resolve("POST", WebApiRoutes.Events, false, true, true).Kind);
    }

    private static void SecureShellAssetsAreClassified()
    {
        Equal(WebRequestLifecycleKind.WebManifest,
            Resolve("HEAD", "/manifest.webmanifest?token=test", true, true, true).Kind);
        Equal(WebRequestLifecycleKind.AppIcon,
            Resolve("GET", "/app-icon-192.png?token=test", true, true, true).Kind);
        Equal(WebRequestLifecycleKind.ServiceWorker,
            Resolve("GET", "/service-worker.js?token=test", true, true, true).Kind);
        Equal(WebRequestLifecycleKind.WebShell,
            Resolve("GET", "/broadcast/42?token=test", true, true, true).Kind);
    }

    private static void RemainingRequestsReachApiDispatch()
    {
        var decision = Resolve("GET", WebApiRoutes.Broadcasts + "?token=test&view=recent", true, true, true);
        Equal(WebRequestLifecycleKind.AuthorizedRoute, decision.Kind);
        Equal(WebApiRoutes.Broadcasts, decision.Context?.Path);
        Equal("recent", decision.Context?.Query["view"]);
    }

    private static WebRequestLifecycleDecision Resolve(
        string method,
        string target,
        bool secure,
        bool secureAccessEnabled,
        bool authorized)
        => WebRequestLifecycleResolver.Resolve(method, target, secure, secureAccessEnabled, authorized);
}
