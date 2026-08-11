using TheRadioVault.Client.Mobile.Models;

namespace TheRadioVault.Client.Mobile.Explore;

/// <summary>
/// Owns cache-first Explore queries, serialized cache warming and image
/// hydration. UI navigation and archive-wide search remain session concerns.
/// </summary>
internal sealed class MobileExploreQueryCoordinator : IDisposable
{
    private readonly IMobileExploreTransport _transport;
    private readonly MobileMetadataCache _cache;
    private readonly Func<IDisposable> _beginSynchronizationActivity;
    private readonly Action _stateChanged;
    private readonly Func<DateOnly> _today;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public MobileExploreQueryCoordinator(
        IMobileExploreTransport transport,
        MobileMetadataCache cache,
        Func<IDisposable> beginSynchronizationActivity,
        Action stateChanged,
        Func<DateOnly>? today = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _beginSynchronizationActivity = beginSynchronizationActivity ??
            throw new ArgumentNullException(nameof(beginSynchronizationActivity));
        _stateChanged = stateChanged ?? throw new ArgumentNullException(nameof(stateChanged));
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Today));
    }

    public async Task<MobileExploreDashboard?> LoadDashboardAsync(
        bool isPaired,
        bool isLiveConnected,
        bool isBusy)
    {
        var cached = BuildDashboard();
        if (cached is not null)
        {
            if (isLiveConnected) _ = RefreshCacheAsync(warmEntireCache: false, isPaired, isLiveConnected);
            return cached;
        }
        if (!isPaired || isBusy) return null;
        await RefreshCacheAsync(warmEntireCache: false, isPaired, isLiveConnected).ConfigureAwait(false);
        return BuildDashboard();
    }

    public async Task<MobileExplorePageLoadResult> LoadPageAsync(
        Guid pageId,
        bool isPaired,
        bool isLiveConnected,
        bool isBusy)
    {
        var cached = _cache.FindExploreDocument(pageId);
        if (cached is not null)
        {
            if (isLiveConnected) _ = RefreshPageAsync(pageId, isLiveConnected);
            return new MobileExplorePageLoadResult(cached, null);
        }
        if (!isPaired || isBusy) return new MobileExplorePageLoadResult(null, null);
        try
        {
            var page = await _transport.GetPageAsync(pageId).ConfigureAwait(false);
            if (page is not null)
            {
                _cache.UpsertExploreDocuments([page]);
                await _cache.SaveAsync().ConfigureAwait(false);
            }
            return new MobileExplorePageLoadResult(
                page,
                page is null ? "That Explore article could not be found." : $"Loaded {page.Title}.");
        }
        catch (Exception exception)
        {
            return new MobileExplorePageLoadResult(null, "Explore article failed: " + exception.Message);
        }
    }

    public async Task<IReadOnlyList<MobileExploreImage>> LoadImagesAsync(
        MobileWikiPageDocument document,
        bool isLiveConnected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        var images = new List<MobileExploreImage>();
        foreach (var link in document.Images.OrderBy(value => value.SortOrder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = _cache.ReadImage(link.ImageId);
            if ((bytes is null || bytes.Length == 0) && isLiveConnected)
            {
                try
                {
                    var downloaded = await _transport
                        .GetImageAsync(link.ImageId, cancellationToken)
                        .ConfigureAwait(false);
                    if (downloaded is not null)
                    {
                        await _cache.SaveImageAsync(downloaded).ConfigureAwait(false);
                        bytes = downloaded.Content;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    System.Diagnostics.Trace.WriteLine($"[iOS Explore image] {exception}");
                }
            }
            if (bytes is null || bytes.Length == 0) continue;
            images.Add(ToExploreImage(document, link, bytes));
        }
        return images;
    }

    public async Task RefreshCacheAsync(
        bool warmEntireCache,
        bool isPaired,
        bool isLiveConnected,
        CancellationToken cancellationToken = default)
    {
        if (!isPaired || !isLiveConnected) return;
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        using var activity = _beginSynchronizationActivity();
        try
        {
            var overview = await _transport.GetOverviewAsync(cancellationToken).ConfigureAwait(false);
            var pages = await _transport.BrowseAsync(5000, cancellationToken).ConfigureAwait(false);
            var today = _today();
            var highlights = await _transport
                .GetDashboardHighlightsAsync(today.Month, today.Day, cancellationToken)
                .ConfigureAwait(false);
            _cache.SetExplore(overview, pages, highlights);

            var existing = _cache.Snapshot.ExploreDocuments.ToDictionary(value => value.PageId);
            var candidates = warmEntireCache
                ? pages
                : pages.Where(value => value.ImageCount > 0)
                    .OrderByDescending(value => value.Status.Equals("Published", StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(value => value.ImageCount)
                    .Take(8)
                    .ToArray();
            var refreshed = new List<MobileWikiPageDocument>();
            foreach (var page in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (existing.TryGetValue(page.PageId, out var document) && document.Revision == page.Revision)
                {
                    refreshed.Add(document);
                    continue;
                }
                var loaded = await _transport.GetPageAsync(page.PageId, cancellationToken).ConfigureAwait(false);
                if (loaded is not null) refreshed.Add(loaded);
            }
            _cache.UpsertExploreDocuments(refreshed);

            foreach (var imageId in refreshed
                         .SelectMany(value => value.Images)
                         .Select(value => value.ImageId)
                         .Distinct())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_cache.HasImage(imageId)) continue;
                var image = await _transport.GetImageAsync(imageId, cancellationToken).ConfigureAwait(false);
                if (image is not null) await _cache.SaveImageAsync(image).ConfigureAwait(false);
            }
            await _cache.SaveAsync().ConfigureAwait(false);
            _stateChanged();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[iOS Explore cache] {exception}");
        }
        finally
        {
            _gate.Release();
        }
    }

    public MobileExploreDashboard? BuildDashboard()
    {
        var snapshot = _cache.Snapshot;
        if (snapshot.ExploreOverview is not { } overview ||
            snapshot.ExploreHighlights is not { } highlights) return null;
        var all = snapshot.ExplorePages;
        var featured = all
            .OrderByDescending(value => value.Status.Equals("Published", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(value => value.CitationCount + value.ImageCount + value.TimelineEventCount)
            .ThenByDescending(value => value.UpdatedAt)
            .Take(6)
            .ToArray();
        var order = featured
            .Select((value, index) => (value.PageId, index))
            .ToDictionary(value => value.PageId, value => value.index);
        var gallery = snapshot.ExploreDocuments
            .Where(value => value.Images.Count > 0)
            .OrderBy(value => order.GetValueOrDefault(value.PageId, int.MaxValue))
            .ThenBy(value => value.Title)
            .SelectMany(document => document.Images.OrderBy(link => link.SortOrder).Take(1)
                .Select(link => (document, link, bytes: _cache.ReadImage(link.ImageId))))
            .Where(value => value.bytes is { Length: > 0 })
            .Take(8)
            .Select(value => ToExploreImage(value.document, value.link, value.bytes!))
            .ToArray();
        return new MobileExploreDashboard(
            overview,
            all,
            featured,
            all.OrderByDescending(value => value.UpdatedAt).Take(8).ToArray(),
            all.Where(value => value.PageType.Equals("Show", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(value => value.TimelineEventCount).ThenBy(value => value.Title).Take(10).ToArray(),
            all.Where(value => value.PageType.Equals("Person", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(value => value.CitationCount).ThenBy(value => value.Title).Take(10).ToArray(),
            all.Where(value => value.PageType.Equals("Topic", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(value => value.CitationCount).ThenBy(value => value.Title).Take(10).ToArray(),
            all.Where(value => value.PageType.Equals("Show", StringComparison.OrdinalIgnoreCase) &&
                               value.TimelineEventCount > 0)
                .OrderBy(value => value.Title, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            highlights,
            gallery);
    }

    private async Task RefreshPageAsync(Guid pageId, bool isLiveConnected)
    {
        if (!isLiveConnected) return;
        try
        {
            var page = await _transport.GetPageAsync(pageId).ConfigureAwait(false);
            if (page is null) return;
            _cache.UpsertExploreDocuments([page]);
            await LoadImagesAsync(page, isLiveConnected).ConfigureAwait(false);
            await _cache.SaveAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[iOS Explore page refresh] {exception}");
        }
    }

    private static MobileExploreImage ToExploreImage(
        MobileWikiPageDocument document,
        MobileWikiPageImageLink link,
        byte[] bytes)
        => new(
            link.ImageId,
            document.PageId,
            document.Title,
            string.IsNullOrWhiteSpace(link.Image?.Caption) ? document.Title : link.Image.Caption,
            string.IsNullOrWhiteSpace(link.Image?.AltText) ? document.Title : link.Image.AltText,
            link.Image?.MediaType ?? "image/jpeg",
            bytes);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}

internal sealed record MobileExplorePageLoadResult(
    MobileWikiPageDocument? Page,
    string? Status);

internal interface IMobileExploreTransport
{
    Task<MobileWikiOverview> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MobileWikiPageSummary>> BrowseAsync(
        int limit,
        CancellationToken cancellationToken = default);
    Task<MobileWikiDashboardHighlights> GetDashboardHighlightsAsync(
        int month,
        int day,
        CancellationToken cancellationToken = default);
    Task<MobileWikiPageDocument?> GetPageAsync(
        Guid pageId,
        CancellationToken cancellationToken = default);
    Task<MobileWikiImageContent?> GetImageAsync(
        Guid imageId,
        CancellationToken cancellationToken = default);
}

internal sealed class MobileExploreTransport(MobileServerClient server) : IMobileExploreTransport
{
    private readonly MobileServerClient _server = server ?? throw new ArgumentNullException(nameof(server));

    public Task<MobileWikiOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        => _server.GetWikiOverviewAsync(cancellationToken);
    public Task<IReadOnlyList<MobileWikiPageSummary>> BrowseAsync(
        int limit,
        CancellationToken cancellationToken = default)
        => _server.BrowseWikiAsync(limit: limit, cancellationToken: cancellationToken);
    public Task<MobileWikiDashboardHighlights> GetDashboardHighlightsAsync(
        int month,
        int day,
        CancellationToken cancellationToken = default)
        => _server.GetWikiDashboardHighlightsAsync(month, day, cancellationToken);
    public Task<MobileWikiPageDocument?> GetPageAsync(
        Guid pageId,
        CancellationToken cancellationToken = default)
        => _server.GetWikiPageAsync(pageId, cancellationToken);
    public Task<MobileWikiImageContent?> GetImageAsync(
        Guid imageId,
        CancellationToken cancellationToken = default)
        => _server.GetWikiImageAsync(imageId, cancellationToken);
}
