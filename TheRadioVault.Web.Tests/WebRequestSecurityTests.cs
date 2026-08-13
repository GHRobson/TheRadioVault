using TheRadioVault.Web.Models;
using TheRadioVault.Web.Services;
using static TheRadioVault.Web.Tests.TestAssert;

namespace TheRadioVault.Web.Tests;

internal static class WebRequestSecurityTests
{
    public static IReadOnlyList<(string Name, Action Run)> Cases { get; } =
    [
        ("Request authorization accepts only server or paired credentials", RequestAuthorizationAcceptsTrustedCredentials),
        ("Desktop pairing expires and normalizes trusted clients", DesktopPairingExpiresAndNormalizesClients),
        ("Mutation ledger validates ids, persists acknowledgements and evicts oldest entries", MutationLedgerPersistsAndEvicts)
    ];

    private static void RequestAuthorizationAcceptsTrustedCredentials()
    {
        const string serverToken = "server-token-0123456789abcdef";
        const string pairedToken = "paired-token-0123456789abcdef0123456789";
        var clients = new[] { new WebPairedDesktopClient("laptop-01", "Laptop", pairedToken, DateTimeOffset.UtcNow) };

        using var queryRequest = Request();
        True(WebRequestAuthorizer.IsAuthorized(queryRequest,
            new Dictionary<string, string> { ["token"] = serverToken }, serverToken, clients));

        using var directRequest = Request(("X-RadioVault-Token", pairedToken));
        True(WebRequestAuthorizer.IsAuthorized(directRequest, EmptyQuery, serverToken, clients));

        using var bearerRequest = Request(("Authorization", $"Bearer {serverToken}"));
        True(WebRequestAuthorizer.IsAuthorized(bearerRequest, EmptyQuery, serverToken, clients));

        using var rejected = Request(("Authorization", "Bearer untrusted"));
        True(!WebRequestAuthorizer.IsAuthorized(rejected, EmptyQuery, serverToken, clients));
    }

    private static void DesktopPairingExpiresAndNormalizesClients()
    {
        var now = new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);
        const string token = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var pairing = new WebDesktopPairingCoordinator(
            utcNow: () => now,
            codeFactory: () => "123456",
            tokenFactory: () => token);

        Equal("123456", pairing.Begin().Code);
        Equal(WebPairingDecisionKind.InvalidCode,
            pairing.TryPair(new WebDesktopPairingRequest("123", "laptop-01", "Laptop")).Kind);
        Equal(WebPairingDecisionKind.InvalidIdentity,
            pairing.TryPair(new WebDesktopPairingRequest("123456", "bad id!", "Laptop")).Kind);
        var accepted = pairing.TryPair(new WebDesktopPairingRequest("123456", " laptop-01 ", " Laptop "));
        Equal(WebPairingDecisionKind.Paired, accepted.Kind);
        Equal("laptop-01", accepted.Client?.ClientId);
        Equal("Laptop", accepted.Client?.DisplayName);
        Equal(token, accepted.Client?.Token);
        True(pairing.Current is null);
        Equal(1, pairing.Count);

        pairing.Begin();
        now = now.AddMinutes(6);
        Equal(WebPairingDecisionKind.Rejected,
            pairing.TryPair(new WebDesktopPairingRequest("123456", "desktop-02", "Desktop")).Kind);
        True(pairing.Current is null);
        True(pairing.Revoke("laptop-01"));
        Equal(0, pairing.Count);
    }

    private static void MutationLedgerPersistsAndEvicts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "RadioVaultWebTests", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "mutation-acks.json");
        try
        {
            var ledger = new WebMutationLedger(capacity: 2, path: path);
            using var invalid = Request(("X-Radio-Vault-Mutation-Id", "bad id"));
            ledger.Record(invalid);
            True(!ledger.Contains(invalid));

            using var first = Mutation("device:first", "iphone-01");
            using var second = Mutation("device:second", "iphone-01");
            using var third = Mutation("device:third", "laptop-01");
            ledger.Record(first, new DateTimeOffset(2026, 8, 13, 10, 0, 0, TimeSpan.Zero));
            ledger.Record(second, new DateTimeOffset(2026, 8, 13, 10, 1, 0, TimeSpan.Zero));
            True(ledger.Contains(first));
            True(ledger.Contains(second));

            var reloaded = new WebMutationLedger(capacity: 2, path: path);
            True(reloaded.Contains(first));
            True(reloaded.Contains(second));
            Equal(2, reloaded.GetDeviceStatuses().Single().AcknowledgedChanges);

            reloaded.Record(third, new DateTimeOffset(2026, 8, 13, 10, 2, 0, TimeSpan.Zero));
            True(!reloaded.Contains(first));
            True(reloaded.Contains(second));
            True(reloaded.Contains(third));
            Equal(2, reloaded.GetDeviceStatuses().Count);
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyQuery =
        new Dictionary<string, string>();

    private static HttpRequest Mutation(string mutationId, string clientId)
        => Request(
            ("X-Radio-Vault-Mutation-Id", mutationId),
            ("X-RadioVault-Client-Id", clientId));

    private static HttpRequest Request(params (string Name, string Value)[] headers)
        => new("GET", "/", headers.ToDictionary(x => x.Name, x => x.Value, StringComparer.OrdinalIgnoreCase), []);
}
