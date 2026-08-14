using System.Text.RegularExpressions;

namespace TheRadioVault.Client.Mobile.Diagnostics;

public static partial class MobileDiagnosticRedactor
{
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value ?? string.Empty;
        var redacted = AuthorizationRegex().Replace(value, "$1<redacted>");
        redacted = SecretRegex().Replace(redacted, "$1<redacted>");
        redacted = UrlAuthorityRegex().Replace(redacted, "$1://<server>");
        return IpAddressRegex().Replace(redacted, "<server>");
    }

    [GeneratedRegex(@"(?im)\b(authorization\s*:\s*(?:bearer\s+)?)[^\s,;]+")]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(@"(?im)\b((?:access[-_ ]?)?token|pairing[-_ ]?code|password|secret)(\s*[:=]\s*)[^\s,;&]+")]
    private static partial Regex SecretRegex();

    [GeneratedRegex(@"(?i)\b(https?|wss?)://(?:\[[^\]]+\]|[^\s/:]+)(?::\d+)?")]
    private static partial Regex UrlAuthorityRegex();

    [GeneratedRegex(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?(?![\d.])")]
    private static partial Regex IpAddressRegex();
}
