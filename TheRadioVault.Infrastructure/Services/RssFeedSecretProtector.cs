using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services;

internal sealed class RssFeedSecretProtector
{
    private const byte FormatVersion = 1;
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("RadioVault.RssFeedSource.v1");
    private readonly byte[] _key;

    public RssFeedSecretProtector(WebServerPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{preferences.ServerInstanceId}:{preferences.CertificatePassword}:rss-feed-source"));
    }

    public string Protect(RssFeedSource source)
    {
        var normalized = Normalize(source);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(normalized);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];
        using (var aes = new AesGcm(_key, tag.Length))
            aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData);
        var payload = new byte[1 + nonce.Length + tag.Length + ciphertext.Length];
        payload[0] = FormatVersion;
        Buffer.BlockCopy(nonce, 0, payload, 1, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, 13, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, 29, ciphertext.Length);
        CryptographicOperations.ZeroMemory(plaintext);
        return Convert.ToBase64String(payload);
    }

    public RssFeedSource Unprotect(string protectedSource)
    {
        byte[] payload;
        try { payload = Convert.FromBase64String(protectedSource); }
        catch (FormatException exception) { throw new CryptographicException("The saved RSS credentials are damaged.", exception); }
        if (payload.Length < 30 || payload[0] != FormatVersion)
            throw new CryptographicException("The saved RSS credentials use an unsupported format.");
        var plaintext = new byte[payload.Length - 29];
        try
        {
            using var aes = new AesGcm(_key, 16);
            aes.Decrypt(payload.AsSpan(1, 12), payload.AsSpan(29), payload.AsSpan(13, 16), plaintext, AssociatedData);
            return Normalize(JsonSerializer.Deserialize<RssFeedSource>(plaintext)
                ?? throw new CryptographicException("The saved RSS credentials are empty."));
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public static RssFeedSource Normalize(RssFeedSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Uri.TryCreate(source.FeedUrl?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("Enter a complete HTTP or HTTPS RSS feed address.", nameof(source));
        if (string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException("The RSS feed address has no host name.", nameof(source));

        var username = source.Username?.Trim() ?? string.Empty;
        var password = source.Password ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(uri.UserInfo))
        {
            var parts = uri.UserInfo.Split(':', 2);
            if (username.Length == 0) username = Uri.UnescapeDataString(parts[0]);
            if (password.Length == 0 && parts.Length > 1) password = Uri.UnescapeDataString(parts[1]);
            uri = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri;
        }
        if (username.Length > 256 || password.Length > 2048)
            throw new ArgumentException("The RSS feed credentials are too long.", nameof(source));
        return new RssFeedSource(uri.AbsoluteUri, username, password);
    }

    public static string DisplayUrl(RssFeedSource source)
    {
        var uri = new Uri(Normalize(source).FeedUrl);
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.IdnHost}{port}/…";
    }
}
