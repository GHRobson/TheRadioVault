using System.Security.Cryptography;
using TheRadioVault.Media.Contracts;
using TheRadioVault.Media.Models;

namespace TheRadioVault.Media.Services;

public sealed class MediaFingerprintService : IMediaFingerprintService
{
    private const int SampleSize = 64 * 1024;

    public MediaFingerprint Create(string path, bool includeFullHash = false)
    {
        var before = new FileInfo(path);
        var beforeLength = before.Length;
        var beforeModified = before.LastWriteTimeUtc;
        // Identity evidence must describe one stable byte sequence. Sharing
        // reads is harmless, but allowing another writer during sampling could
        // persist a fingerprint assembled from two versions of the file.
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != beforeLength)
            throw new IOException("The media file changed before fingerprinting could start.");
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

        var after = new FileInfo(path);
        if (stream.Length != beforeLength || after.Length != beforeLength || after.LastWriteTimeUtc != beforeModified)
            throw new IOException("The media file changed while its identity was being calculated. Try the scan again.");
        return new MediaFingerprint(beforeLength, partialHash, fullHash);
    }

    private static void AppendSample(Stream stream, IncrementalHash hash, long position)
    {
        stream.Position = position;
        var buffer = new byte[SampleSize];
        var read = stream.Read(buffer, 0, buffer.Length);
        if (read > 0) hash.AppendData(buffer, 0, read);
    }
}
