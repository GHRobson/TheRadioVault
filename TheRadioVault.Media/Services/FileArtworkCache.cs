using System.Security.Cryptography;
using TheRadioVault.Media.Contracts;

namespace TheRadioVault.Media.Services;

public sealed class FileArtworkCache : IArtworkCache
{
    private readonly string _cacheDirectory;

    public FileArtworkCache(string cacheDirectory)
    {
        _cacheDirectory = cacheDirectory ?? throw new ArgumentNullException(nameof(cacheDirectory));
    }

    public string? Store(byte[]? artworkBytes, string? mimeType)
    {
        if (artworkBytes is not { Length: > 0 }) return null;
        Directory.CreateDirectory(_cacheDirectory);
        var hash = Convert.ToHexString(SHA256.HashData(artworkBytes)).ToLowerInvariant();
        var extension = mimeType?.ToLowerInvariant() switch
        {
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            _ => ".jpg"
        };
        var path = Path.Combine(_cacheDirectory, hash + extension);
        if (!File.Exists(path)) File.WriteAllBytes(path, artworkBytes);
        return path;
    }
}
