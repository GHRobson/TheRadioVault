using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Web.Services;

internal static class WebRequestAuthorizer
{
    public static bool IsAuthorized(
        HttpRequest request,
        IReadOnlyDictionary<string, string> query,
        string accessToken,
        IEnumerable<WebPairedDesktopClient> pairedClients)
    {
        if (query.TryGetValue("token", out var queryToken) && SecureEquals(queryToken, accessToken))
            return true;

        string? headerToken = null;
        if (request.Headers.TryGetValue("X-RadioVault-Token", out var directToken))
            headerToken = directToken.Trim();
        else if (request.Headers.TryGetValue("Authorization", out var authorization) &&
                 authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            headerToken = authorization[7..].Trim();

        if (string.IsNullOrWhiteSpace(headerToken)) return false;
        if (SecureEquals(headerToken, accessToken)) return true;
        return pairedClients.Any(client => SecureEquals(headerToken, client.Token));
    }

    public static bool SecureEquals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}

internal enum WebPairingDecisionKind
{
    Paired,
    InvalidCode,
    InvalidIdentity,
    Rejected
}

internal sealed record WebPairingDecision(
    WebPairingDecisionKind Kind,
    string Message,
    WebPairedDesktopClient? Client = null);

internal sealed class WebDesktopPairingCoordinator
{
    private readonly ConcurrentDictionary<string, WebPairedDesktopClient> _clients = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string> _codeFactory;
    private readonly Func<string> _tokenFactory;
    private string _code = string.Empty;
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private int _attemptsRemaining;

    public WebDesktopPairingCoordinator(
        IEnumerable<WebPairedDesktopClient>? clients = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string>? codeFactory = null,
        Func<string>? tokenFactory = null)
    {
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _codeFactory = codeFactory ?? (() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture));
        _tokenFactory = tokenFactory ?? (() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant());
        Replace(clients ?? []);
    }

    public int Count => _clients.Count;

    public IReadOnlyList<WebPairedDesktopClient> Clients => _clients.Values
        .OrderBy(client => client.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public WebDesktopPairingSession? Current
    {
        get
        {
            lock (_gate)
                return IsActiveUnsafe() ? new WebDesktopPairingSession(_code, _expiresAt) : null;
        }
    }

    public WebDesktopPairingSession Begin()
    {
        lock (_gate)
        {
            _code = _codeFactory();
            _expiresAt = _utcNow().AddMinutes(5);
            _attemptsRemaining = 10;
            return new WebDesktopPairingSession(_code, _expiresAt);
        }
    }

    public void Cancel()
    {
        lock (_gate) ClearUnsafe();
    }

    public bool Revoke(string clientId)
        => !string.IsNullOrWhiteSpace(clientId) && _clients.TryRemove(clientId.Trim(), out _);

    public void Replace(IEnumerable<WebPairedDesktopClient> clients)
    {
        _clients.Clear();
        foreach (var client in clients ?? [])
        {
            var clientId = NormalizeClientId(client.ClientId);
            var displayName = NormalizeDisplayName(client.DisplayName);
            var token = client.Token?.Trim() ?? string.Empty;
            if (clientId.Length == 0 || displayName.Length == 0 || token.Length < 32) continue;
            _clients[clientId] = client with { ClientId = clientId, DisplayName = displayName, Token = token };
        }
    }

    public WebPairingDecision TryPair(WebDesktopPairingRequest request)
    {
        var code = request.Code?.Trim() ?? string.Empty;
        if (code.Length != 6 || code.Any(ch => !char.IsDigit(ch)))
            return new(WebPairingDecisionKind.InvalidCode, "The pairing code must contain exactly six digits.");

        var clientId = NormalizeClientId(request.ClientId);
        var displayName = NormalizeDisplayName(request.DisplayName);
        if (clientId.Length == 0 || displayName.Length == 0)
            return new(WebPairingDecisionKind.InvalidIdentity, "The remote-client identity is invalid. Restart Radio Vault on the remote client and generate a fresh pairing code.");

        lock (_gate)
        {
            if (IsActiveUnsafe() && WebRequestAuthorizer.SecureEquals(code, _code))
            {
                var client = new WebPairedDesktopClient(clientId, displayName, _tokenFactory(), _utcNow());
                _clients[clientId] = client;
                ClearUnsafe();
                return new(WebPairingDecisionKind.Paired, "This remote client is now trusted by the Radio Vault server.", client);
            }

            _attemptsRemaining = Math.Max(0, _attemptsRemaining - 1);
            if (_attemptsRemaining == 0) ClearUnsafe();
            return new(WebPairingDecisionKind.Rejected, "The pairing code is invalid or has expired.");
        }
    }

    private bool IsActiveUnsafe()
    {
        if (string.IsNullOrWhiteSpace(_code) || _attemptsRemaining <= 0) return false;
        if (_expiresAt > _utcNow()) return true;
        ClearUnsafe();
        return false;
    }

    private void ClearUnsafe()
    {
        _code = string.Empty;
        _expiresAt = DateTimeOffset.MinValue;
        _attemptsRemaining = 0;
    }

    private static string NormalizeClientId(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is < 8 or > 128) return string.Empty;
        return trimmed.All(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.') ? trimmed : string.Empty;
    }

    private static string NormalizeDisplayName(string? value)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        if (trimmed.Length is < 1 or > 80) return string.Empty;
        return new string(trimmed.Where(ch => !char.IsControl(ch)).ToArray()).Trim();
    }
}

internal sealed class WebMutationLedger(int capacity = 2048)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _processed = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _order = new();
    private readonly int _capacity = Math.Max(1, capacity);

    public bool Contains(HttpRequest request)
        => TryGetMutationId(request, out var mutationId) && _processed.ContainsKey(mutationId);

    public void Record(HttpRequest request, DateTimeOffset? processedAt = null)
    {
        if (!TryGetMutationId(request, out var mutationId)) return;
        if (!_processed.TryAdd(mutationId, processedAt ?? DateTimeOffset.UtcNow)) return;
        _order.Enqueue(mutationId);
        while (_processed.Count > _capacity && _order.TryDequeue(out var oldest))
            _processed.TryRemove(oldest, out _);
    }

    public static bool TryGetMutationId(HttpRequest request, out string mutationId)
    {
        mutationId = string.Empty;
        if (!request.Headers.TryGetValue("X-Radio-Vault-Mutation-Id", out var raw)) return false;
        var value = raw.Trim();
        if (value.Length is < 8 or > 128) return false;
        if (value.Any(ch => !char.IsLetterOrDigit(ch) && ch is not ('-' or '_' or '.' or ':'))) return false;
        mutationId = value;
        return true;
    }
}
