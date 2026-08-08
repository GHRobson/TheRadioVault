using System.Security.Cryptography;
using TheRadioVault.Media.Contracts;
using TheRadioVault.Media.Models;

namespace TheRadioVault.Media.Services;

public sealed class MediaFingerprintService : IMediaFingerprintService
{
    private const int SampleSize = 64 * 1024;

    public MediaFingerprint Create(string path, bool includeFullHash = false)
    {
        var info = new FileInfo(path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var partial = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendSample(stream, partial, 0);
        if (stream.Length > SampleSize * 2L) AppendSample(stream, partial, Math.Max(0, stream.Length - SampleSize));
        var partialHash = Convert.ToHexString(partial.GetHashAndReset()).ToLowerInvariant();

        string? fullHash = null;
        if (includeFullHash)
        {
            stream.Position = 0;
            fullHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }

        return new MediaFingerprint(info.Length, partialHash, fullHash);
    }

    private static void AppendSample(Stream stream, IncrementalHash hash, long position)
    {
        stream.Position = position;
        var buffer = new byte[SampleSize];
        var read = stream.Read(buffer, 0, buffer.Length);
        if (read > 0) hash.AppendData(buffer, 0, read);
    }
}
