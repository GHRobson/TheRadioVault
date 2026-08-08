using System.Security.Cryptography;
using System.Text;

namespace TheRadioVault.Services;

/// <summary>
/// Bounded encrypted cache for authenticated GET responses from one paired
/// server. Its key is derived from that client's private pairing token, so a
/// copied cache is unreadable without the saved relationship.
/// </summary>
public sealed class NativeServerResponseCache
{
    private const byte FormatVersion = 1;
    private const long MaximumBytes = 50L * 1024 * 1024;
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly byte[] _key;

    public NativeServerResponseCache(NativeServerConnectionPreferences preferences, string? cacheRoot = null)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        _directory = DirectoryFor(preferences.ServerInstanceId, cacheRoot);
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(
            preferences.ServerInstanceId + ":" + preferences.AccessToken));
    }

    public long SizeBytes
    {
        get
        {
            try
            {
                return Directory.Exists(_directory)
                    ? Directory.EnumerateFiles(_directory, "*.rvcache").Sum(path => new FileInfo(path).Length)
                    : 0;
            }
            catch { return 0; }
        }
    }

    public void Store(string path, byte[] plaintext)
    {
        if (plaintext.Length == 0) return;
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                var nonce = RandomNumberGenerator.GetBytes(12);
                var tag = new byte[16];
                var ciphertext = new byte[plaintext.Length];
                using (var aes = new AesGcm(_key, tag.Length))
                    aes.Encrypt(nonce, plaintext, ciphertext, tag, Encoding.UTF8.GetBytes(path));
                var payload = new byte[1 + nonce.Length + tag.Length + ciphertext.Length];
                payload[0] = FormatVersion;
                Buffer.BlockCopy(nonce, 0, payload, 1, nonce.Length);
                Buffer.BlockCopy(tag, 0, payload, 1 + nonce.Length, tag.Length);
                Buffer.BlockCopy(ciphertext, 0, payload, 1 + nonce.Length + tag.Length, ciphertext.Length);
                var target = FileFor(path);
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllBytes(temporary, payload);
                File.Move(temporary, target, overwrite: true);
                File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
                TrimUnsafe();
            }
            catch (Exception exception)
            {
                DiagnosticLog.Write("Native server cache", "A server response could not be cached.", exception);
            }
        }
    }

    public bool TryLoad(string path, out byte[] plaintext)
    {
        plaintext = Array.Empty<byte>();
        lock (_gate)
        {
            try
            {
                var target = FileFor(path);
                if (!File.Exists(target)) return false;
                var payload = File.ReadAllBytes(target);
                if (payload.Length < 30 || payload[0] != FormatVersion) return false;
                var nonce = payload.AsSpan(1, 12);
                var tag = payload.AsSpan(13, 16);
                var ciphertext = payload.AsSpan(29);
                plaintext = new byte[ciphertext.Length];
                using (var aes = new AesGcm(_key, tag.Length))
                    aes.Decrypt(nonce, ciphertext, tag, plaintext, Encoding.UTF8.GetBytes(path));
                File.SetLastAccessTimeUtc(target, DateTime.UtcNow);
                return true;
            }
            catch (CryptographicException) { return false; }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
    }

    public static void DeleteForServer(string? serverInstanceId)
    {
        try
        {
            var directory = DirectoryFor(serverInstanceId, cacheRoot: null);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private string FileFor(string path)
        => Path.Combine(_directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))) + ".rvcache");

    private static string DirectoryFor(string? serverInstanceId, string? cacheRoot)
    {
        var identity = new string((serverInstanceId ?? "unknown")
            .Where(ch => char.IsLetterOrDigit(ch) || ch == '-').ToArray());
        if (identity.Length == 0) identity = "unknown";
        return Path.Combine(cacheRoot ?? Path.Combine(AppPaths.DataDirectory, "NativeServerCache"), identity);
    }

    private void TrimUnsafe()
    {
        var files = Directory.EnumerateFiles(_directory, "*.rvcache")
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastAccessTimeUtc)
            .ToArray();
        var total = files.Sum(file => file.Length);
        foreach (var file in files)
        {
            if (total <= MaximumBytes) break;
            total -= file.Length;
            try { file.Delete(); } catch { }
        }
    }
}
