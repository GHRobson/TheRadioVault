using TheRadioVault.Web.Contracts;

namespace TheRadioVault.Web.Services;

internal enum WebRequestLifecycleKind
{
    InvalidMethod,
    MalformedTarget,
    Pairing,
    Unauthorized,
    SecureSetup,
    SecureProfile,
    SecureRootCertificate,
    RedirectToSecure,
    WebManifest,
    AppIcon,
    ServiceWorker,
    WebShell,
    AuthorizedRoute
}

internal sealed record WebRequestContext(
    WebRequestMethod Method,
    string Path,
    IReadOnlyDictionary<string, string> Query)
{
    public bool IsGet => Method == WebRequestMethod.Get;
    public bool IsHead => Method == WebRequestMethod.Head;
    public bool IsPost => Method == WebRequestMethod.Post;
    public bool IsRead => IsGet || IsHead;
}

internal readonly record struct WebRequestLifecycleDecision(
    WebRequestLifecycleKind Kind,
    WebRequestContext? Context = null);

/// <summary>
/// Pure request-lifecycle policy. It owns the order-sensitive boundary between
/// unauthenticated pairing, authorization, HTTP-to-HTTPS setup, static shell
/// resources and authenticated API dispatch. Network IO and response payloads
/// remain in <see cref="LocalWebServer"/>.
/// </summary>
internal static class WebRequestLifecycleResolver
{
    private static readonly HashSet<string> AppIconPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/app-icon-180.png",
        "/app-icon-192.png",
        "/app-icon-512.png",
        "/app-icon-maskable-512.png",
        "/app-logo-512.png"
    };

    public static WebRequestLifecycleDecision Resolve(
        string method,
        string target,
        bool secure,
        bool secureAccessEnabled,
        bool authorized)
    {
        if (!WebApiRouteResolver.TryParseMethod(method, out var parsedMethod))
            return new(WebRequestLifecycleKind.InvalidMethod);

        if (!TryParseTarget(target, parsedMethod, secure, out var context))
            return new(WebRequestLifecycleKind.MalformedTarget);

        if (context.Path.Equals(WebApiRoutes.FederationPair, StringComparison.OrdinalIgnoreCase))
            return new(WebRequestLifecycleKind.Pairing, context);

        if (!authorized)
            return new(WebRequestLifecycleKind.Unauthorized, context);

        if (secureAccessEnabled && !secure)
        {
            if (context.IsRead && context.Path.Equals("/secure-setup", StringComparison.OrdinalIgnoreCase))
                return new(WebRequestLifecycleKind.SecureSetup, context);
            if (context.IsRead && context.Path.Equals("/secure-profile.mobileconfig", StringComparison.OrdinalIgnoreCase))
                return new(WebRequestLifecycleKind.SecureProfile, context);
            if (context.IsRead && context.Path.Equals("/secure-root.cer", StringComparison.OrdinalIgnoreCase))
                return new(WebRequestLifecycleKind.SecureRootCertificate, context);
            return new(WebRequestLifecycleKind.RedirectToSecure, context);
        }

        if (secure && context.IsRead && context.Path.Equals("/manifest.webmanifest", StringComparison.OrdinalIgnoreCase))
            return new(WebRequestLifecycleKind.WebManifest, context);
        if (context.IsRead && AppIconPaths.Contains(context.Path))
            return new(WebRequestLifecycleKind.AppIcon, context);
        if (secure && context.IsRead && context.Path.Equals("/service-worker.js", StringComparison.OrdinalIgnoreCase))
            return new(WebRequestLifecycleKind.ServiceWorker, context);
        if (context.IsRead && IsShellPath(context.Path))
            return new(WebRequestLifecycleKind.WebShell, context);

        return new(WebRequestLifecycleKind.AuthorizedRoute, context);
    }

    private static bool TryParseTarget(
        string target,
        WebRequestMethod method,
        bool secure,
        out WebRequestContext context)
    {
        context = default!;
        if (string.IsNullOrWhiteSpace(target) || !target.StartsWith("/", StringComparison.Ordinal))
            return false;
        if (!Uri.TryCreate((secure ? "https" : "http") + "://radiovault.local" + target,
                UriKind.Absolute, out var uri))
            return false;
        context = new WebRequestContext(method, uri.AbsolutePath, ParseQuery(uri.Query));
        return true;
    }

    private static bool IsShellPath(string path)
        => path == "/" ||
           path.Equals("/index.html", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWith("/broadcast/", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            try
            {
                var key = Uri.UnescapeDataString(parts[0].Replace('+', ' '));
                var value = parts.Length > 1
                    ? Uri.UnescapeDataString(parts[1].Replace('+', ' '))
                    : string.Empty;
                result[key] = value;
            }
            catch (UriFormatException)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        return result;
    }
}
