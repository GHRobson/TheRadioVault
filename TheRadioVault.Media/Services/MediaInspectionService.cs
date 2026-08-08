using TheRadioVault.Media.Contracts;
using TheRadioVault.Media.Models;

namespace TheRadioVault.Media.Services;

/// <summary>
/// Coordinates tag reading, artwork caching and fingerprinting without any UI
/// or operating-system dependencies. Hosts decide whether a file is safe to
/// inspect (for example, avoiding cloud-placeholder hydration).
/// </summary>
public sealed class MediaInspectionService : IMediaInspectionService
{
    private readonly IAudioMetadataReader _metadataReader;
    private readonly IArtworkCache _artworkCache;
    private readonly IMediaFingerprintService _fingerprints;

    public MediaInspectionService(
        IAudioMetadataReader metadataReader,
        IArtworkCache artworkCache,
        IMediaFingerprintService fingerprints)
    {
        _metadataReader = metadataReader;
        _artworkCache = artworkCache;
        _fingerprints = fingerprints;
    }

    public MediaInspection Inspect(string path, bool includeFullHash = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var metadata = _metadataReader.Read(path);
        var artworkPath = _artworkCache.Store(metadata.ArtworkBytes, metadata.ArtworkMimeType);
        var fingerprint = _fingerprints.Create(path, includeFullHash);
        return new MediaInspection(metadata, fingerprint, artworkPath);
    }
}
