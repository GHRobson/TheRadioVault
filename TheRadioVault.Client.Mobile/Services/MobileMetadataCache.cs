using System.Text.Json;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile;

internal sealed class MobileMetadataCache
{
    private readonly string _cachePath;
    private readonly string _imageDirectory;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _gate = new();
    private MobileMetadataCacheSnapshot _snapshot;

    public MobileMetadataCache(string rootPath, string serverInstanceId)
    {
        Directory.CreateDirectory(rootPath);
        _cachePath = Path.Combine(rootPath, "metadata.json");
        _imageDirectory = Path.Combine(rootPath, "ExploreImages");
        Directory.CreateDirectory(_imageDirectory);
        _snapshot = MobileMetadataCacheSnapshot.Empty(serverInstanceId);
    }

    public MobileMetadataCacheSnapshot Snapshot
    {
        get
        {
            lock (_gate) return _snapshot;
        }
    }

    public async Task LoadAsync(string serverInstanceId)
    {
        try
        {
            if (!File.Exists(_cachePath)) return;
            await using var stream = File.OpenRead(_cachePath);
            var value = await JsonSerializer.DeserializeAsync(
                stream,
                MobileJsonContext.Default.MobileMetadataCacheSnapshot).ConfigureAwait(false);
            if (value is null || value.Version != 1 ||
                !string.Equals(value.ServerInstanceId, serverInstanceId, StringComparison.Ordinal)) return;
            lock (_gate) _snapshot = value;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[iOS metadata cache load] {exception}");
        }
    }

    public void ApplyLibrarySync(
        MobileLibrarySync sync,
        IReadOnlyList<WebClientLibraryBroadcastSummary>? completeLibrary,
        IReadOnlyList<WebClientLibraryBroadcastSummary> changedBroadcasts,
        IReadOnlySet<long> deletedEpisodeIds,
        WebClientLibraryOverview? overview,
        IReadOnlyList<WebQueueItem>? queue = null)
    {
        lock (_gate)
        {
            var byId = (completeLibrary is not null
                    ? completeLibrary
                    : _snapshot.Broadcasts)
                .ToDictionary(value => value.RepresentativeEpisodeId);
            foreach (var episodeId in deletedEpisodeIds) byId.Remove(episodeId);
            foreach (var broadcast in changedBroadcasts)
                byId[broadcast.RepresentativeEpisodeId] = broadcast;
            _snapshot = _snapshot with
            {
                ServerInstanceId = sync.ServerInstanceId,
                SyncSessionId = sync.SessionId,
                SyncSequence = Math.Max(0, sync.Sequence),
                SyncRevision = sync.LibraryRevision,
                Broadcasts = byId.Values
                    .OrderByDescending(value => value.AirDate)
                    .ThenByDescending(value => value.DateAdded)
                    .ToArray(),
                Overview = overview ?? _snapshot.Overview,
                Queue = queue ?? _snapshot.Queue,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public void ReplaceCompleteLibrary(
        string serverInstanceId,
        IReadOnlyList<WebClientLibraryBroadcastSummary> broadcasts,
        WebClientLibraryOverview overview)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                ServerInstanceId = serverInstanceId,
                Broadcasts = broadcasts
                    .OrderByDescending(value => value.AirDate)
                    .ThenByDescending(value => value.DateAdded)
                    .ToArray(),
                Overview = overview,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public void SetExplore(
        MobileWikiOverview overview,
        IReadOnlyList<MobileWikiPageSummary> pages,
        MobileWikiDashboardHighlights highlights)
    {
        lock (_gate)
        {
            var pageIds = pages.Select(value => value.PageId).ToHashSet();
            _snapshot = _snapshot with
            {
                ExploreOverview = overview,
                ExplorePages = pages,
                ExploreHighlights = highlights,
                ExploreDocuments = _snapshot.ExploreDocuments
                    .Where(value => pageIds.Contains(value.PageId))
                    .ToArray(),
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public void SetQueue(IReadOnlyList<WebQueueItem> queue)
    {
        lock (_gate)
        {
            _snapshot = _snapshot with
            {
                Queue = queue,
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public void UpsertBroadcast(WebClientLibraryBroadcastSummary broadcast)
    {
        lock (_gate)
        {
            var byId = _snapshot.Broadcasts.ToDictionary(value => value.RepresentativeEpisodeId);
            byId[broadcast.RepresentativeEpisodeId] = broadcast;
            _snapshot = _snapshot with
            {
                Broadcasts = byId.Values
                    .OrderByDescending(value => value.AirDate)
                    .ThenByDescending(value => value.DateAdded)
                    .ToArray(),
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public void UpsertExploreDocuments(IEnumerable<MobileWikiPageDocument> documents)
    {
        lock (_gate)
        {
            var byId = _snapshot.ExploreDocuments.ToDictionary(value => value.PageId);
            foreach (var document in documents) byId[document.PageId] = document;
            _snapshot = _snapshot with
            {
                ExploreDocuments = byId.Values.OrderBy(value => value.Title).ToArray(),
                UpdatedAt = DateTimeOffset.UtcNow
            };
        }
    }

    public MobileWikiPageDocument? FindExploreDocument(Guid pageId)
    {
        lock (_gate) return _snapshot.ExploreDocuments.FirstOrDefault(value => value.PageId == pageId);
    }

    public async Task SaveImageAsync(MobileWikiImageContent image)
    {
        if (image.Content.Length == 0) return;
        var path = ImagePath(image.ImageId);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(temporary, image.Content).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    public byte[]? ReadImage(Guid imageId)
    {
        try
        {
            var path = ImagePath(imageId);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[iOS image cache read] {exception}");
            return null;
        }
    }

    public bool HasImage(Guid imageId) => File.Exists(ImagePath(imageId));

    public async Task SaveAsync()
    {
        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            MobileMetadataCacheSnapshot value;
            lock (_gate) value = _snapshot;
            var temporary = _cachePath + ".tmp";
            await using (var stream = File.Create(temporary))
                await JsonSerializer.SerializeAsync(
                    stream,
                    value,
                    MobileJsonContext.Default.MobileMetadataCacheSnapshot).ConfigureAwait(false);
            File.Move(temporary, _cachePath, overwrite: true);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[iOS metadata cache save] {exception}");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Clear()
    {
        lock (_gate) _snapshot = MobileMetadataCacheSnapshot.Empty(string.Empty);
        try
        {
            if (File.Exists(_cachePath)) File.Delete(_cachePath);
            if (Directory.Exists(_imageDirectory)) Directory.Delete(_imageDirectory, recursive: true);
            Directory.CreateDirectory(_imageDirectory);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[iOS metadata cache clear] {exception}");
        }
    }

    private string ImagePath(Guid imageId) => Path.Combine(_imageDirectory, imageId.ToString("N") + ".bin");
}
