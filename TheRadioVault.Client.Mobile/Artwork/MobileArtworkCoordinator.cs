using System.Collections.Concurrent;

namespace TheRadioVault.Client.Mobile.Artwork;

/// <summary>
/// Owns cache-first artwork hydration and coalesces concurrent requests for the
/// same broadcast. Presentation code supplies only a stable episode id; stale
/// metadata must not make an already-cached image disappear.
/// </summary>
internal sealed class MobileArtworkCoordinator
{
    private readonly IMobileArtworkTransport _transport;
    private readonly IMobileArtworkStore _store;
    private readonly ConcurrentDictionary<long, Task<byte[]?>> _requests = new();

    public MobileArtworkCoordinator(IMobileArtworkTransport transport, IMobileArtworkStore store)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _store = store ?? throw new ArgumentNullException(nameof(store));
    }

    public async Task<byte[]?> LoadAsync(long episodeId, bool allowNetwork)
    {
        if (episodeId <= 0) return null;
        var task = _requests.GetOrAdd(
            episodeId,
            id => LoadCoreAsync(id, allowNetwork));
        var content = await task.ConfigureAwait(false);
        if (content is null) _requests.TryRemove(episodeId, out _);
        return content;
    }

    public void Clear() => _requests.Clear();

    private async Task<byte[]?> LoadCoreAsync(long episodeId, bool allowNetwork)
    {
        var cached = _store.Read(episodeId);
        if (cached is { Length: > 0 }) return cached;
        if (!allowNetwork) return null;
        try
        {
            var content = await _transport.GetAsync(episodeId).ConfigureAwait(false);
            if (content.Length == 0) return null;
            await _store.SaveAsync(episodeId, content).ConfigureAwait(false);
            return content;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[iOS artwork load] {exception}");
            return null;
        }
    }
}

internal interface IMobileArtworkTransport
{
    Task<byte[]> GetAsync(long episodeId);
}

internal interface IMobileArtworkStore
{
    byte[]? Read(long episodeId);
    Task SaveAsync(long episodeId, byte[] content);
}

internal sealed class MobileArtworkTransport(MobileServerClient server) : IMobileArtworkTransport
{
    private readonly MobileServerClient _server = server ?? throw new ArgumentNullException(nameof(server));
    public Task<byte[]> GetAsync(long episodeId) => _server.GetArtworkAsync(episodeId);
}

internal sealed class MobileArtworkStore(MobileMetadataCache cache) : IMobileArtworkStore
{
    private readonly MobileMetadataCache _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    public byte[]? Read(long episodeId) => _cache.ReadArtwork(episodeId);
    public Task SaveAsync(long episodeId, byte[] content) => _cache.SaveArtworkAsync(episodeId, content);
}
