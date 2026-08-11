using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile.Library;

/// <summary>
/// Owns cache-first Library projection, filtering, archive grouping and the
/// equivalent live-server query contracts. The session façade retains global
/// busy state and decides which result becomes visible in the UI.
/// </summary>
internal sealed class MobileLibraryQueryCoordinator
{
    private readonly IMobileLibraryQueryTransport _transport;
    private readonly MobileMetadataCache _cache;
    private readonly Func<DateOnly> _today;

    public MobileLibraryQueryCoordinator(
        IMobileLibraryQueryTransport transport,
        MobileMetadataCache cache,
        Func<DateOnly>? today = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _today = today ?? (() => DateOnly.FromDateTime(DateTime.Today));
    }

    public bool HasCachedLibrary => _cache.Snapshot.Broadcasts.Count > 0;

    public MobileLibraryProjection? ProjectSnapshot()
    {
        var snapshot = _cache.Snapshot;
        var broadcasts = snapshot.Broadcasts;
        if (broadcasts.Count == 0)
            return snapshot.Overview is null ? null : FromOverview(snapshot.Overview);

        var today = _today();
        return new MobileLibraryProjection(
            broadcasts.Count,
            broadcasts.Count(value => value.Completed),
            broadcasts.Count(value => value.InProgress && !value.Completed),
            broadcasts.Count(value => value.Favourite),
            BuildCollections(broadcasts),
            BuildCollections(broadcasts.Where(value => !value.Completed)),
            Convert(broadcasts
                .Where(value => value.InProgress && !value.Completed)
                .OrderByDescending(value => value.LastPlayedAt)
                .Take(50)),
            Convert(broadcasts.OrderByDescending(value => value.DateAdded).Take(50)),
            Convert(broadcasts
                .Where(value => value.AirDate is { } date &&
                                date.Month == today.Month && date.Day == today.Day)
                .OrderByDescending(value => value.AirDate)
                .Take(50)),
            Convert(broadcasts
                .Where(value => !value.Completed && !value.InProgress)
                .OrderByDescending(value => value.AirDate)
                .Take(50)),
            Convert(broadcasts.OrderByDescending(value => value.AirDate).Take(250)));
    }

    public MobileLibraryProjection ProjectOnlineOverview(WebClientLibraryOverview overview)
    {
        ArgumentNullException.ThrowIfNull(overview);
        foreach (var broadcast in overview.ContinueListening
                     .Concat(overview.RecentBroadcasts)
                     .Concat(overview.OnThisDay)
                     .GroupBy(value => value.RepresentativeEpisodeId)
                     .Select(group => group.OrderByDescending(value => value.LastPlayedAt).First()))
            _cache.UpsertBroadcast(broadcast);
        return FromOverview(overview);
    }

    public IReadOnlyList<WebClientLibraryCollectionSummary> CollectionsFor(
        IReadOnlyList<WebClientLibraryCollectionSummary> collections,
        IReadOnlyList<WebClientLibraryCollectionSummary> incompleteCollections,
        bool hideCompleted)
        => CombineCollections(hideCompleted ? incompleteCollections : collections);

    public async Task<MobileLibraryBroadcastQueryResult> SearchAsync(
        string? searchText,
        bool hideCompleted,
        CancellationToken cancellationToken = default)
    {
        var search = searchText?.Trim() ?? string.Empty;
        if (HasCachedLibrary)
        {
            var cached = QueryCachedBroadcasts(null, search, "All", null, null, hideCompleted);
            return BroadcastResult(cached, true);
        }
        try
        {
            var result = await _transport.BrowseAsync(
                search,
                100,
                0,
                null,
                "All",
                null,
                null,
                hideCompleted,
                "All",
                false,
                cancellationToken).ConfigureAwait(false);
            return BroadcastResult(Convert(result.Broadcasts), true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new MobileLibraryBroadcastQueryResult([], "Search failed: " + exception.Message, false);
        }
    }

    public async Task<MobileLibraryBroadcastQueryResult> BrowseCollectionAsync(
        int? collectionId,
        string? searchText,
        string filter,
        int? year,
        int? month,
        bool hideCompleted,
        string? collectionName,
        IReadOnlyList<WebClientLibraryCollectionSummary> knownCollections,
        CancellationToken cancellationToken = default)
    {
        var search = searchText?.Trim() ?? string.Empty;
        if (HasCachedLibrary)
        {
            var cached = QueryCachedBroadcasts(
                collectionId, search, filter, year, month, hideCompleted, collectionName);
            return BroadcastResult(cached, true);
        }
        try
        {
            var collectionIds = ResolveCollectionIds(collectionId, collectionName, knownCollections);
            var results = await Task.WhenAll(collectionIds.Select(id => _transport.BrowseAsync(
                search,
                100,
                0,
                id,
                filter,
                year,
                month,
                hideCompleted,
                "All",
                false,
                cancellationToken))).ConfigureAwait(false);
            var broadcasts = Convert(results
                .SelectMany(value => value.Broadcasts)
                .GroupBy(value => value.RepresentativeEpisodeId)
                .Select(group => group.OrderByDescending(value => value.LastPlayedAt).First()));
            return BroadcastResult(broadcasts, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new MobileLibraryBroadcastQueryResult([], "Library failed: " + exception.Message, false);
        }
    }

    public async Task<MobileLibraryArchiveQueryResult> LoadArchivePeriodsAsync(
        int? collectionId,
        int? year,
        bool hideCompleted,
        string? collectionName,
        IReadOnlyList<WebClientLibraryCollectionSummary> knownCollections,
        CancellationToken cancellationToken = default)
    {
        if (HasCachedLibrary)
        {
            var cached = BuildCachedArchivePeriods(collectionId, year, hideCompleted, collectionName);
            return ArchiveResult(cached, true);
        }
        try
        {
            var results = await Task.WhenAll(
                ResolveCollectionIds(collectionId, collectionName, knownCollections)
                    .Select(id => _transport.GetArchivePeriodsAsync(
                        id, year, hideCompleted, cancellationToken))).ConfigureAwait(false);
            return ArchiveResult(CombineArchivePeriods(results.SelectMany(value => value)), true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new MobileLibraryArchiveQueryResult(
                [], "Archive periods failed: " + exception.Message, false);
        }
    }

    public async Task<MobileLibraryExploreQueryResult> ExploreAsync(
        string? searchText,
        int? collectionId,
        string filter,
        int? year,
        string searchScope,
        bool hasTranscript,
        CancellationToken cancellationToken = default)
    {
        var search = searchText?.Trim() ?? string.Empty;
        var hasFilters = collectionId.HasValue || year.HasValue || hasTranscript ||
                         !filter.Equals("All", StringComparison.OrdinalIgnoreCase) ||
                         !searchScope.Equals("All", StringComparison.OrdinalIgnoreCase);
        try
        {
            var facets = await _transport.GetSearchFacetsAsync(cancellationToken).ConfigureAwait(false);
            var suggestions = search.Length < 2
                ? Array.Empty<WebClientLibrarySearchSuggestion>()
                : await _transport.GetSearchSuggestionsAsync(search, cancellationToken).ConfigureAwait(false);
            var results = search.Length == 0 && !hasFilters
                ? Array.Empty<MobileBroadcastItem>()
                : Convert((await _transport.BrowseAsync(
                    search,
                    250,
                    0,
                    collectionId,
                    filter,
                    year,
                    null,
                    false,
                    searchScope,
                    hasTranscript,
                    cancellationToken).ConfigureAwait(false)).Broadcasts);
            var status = search.Length == 0 && !hasFilters
                ? "Choose a way into the archive or browse by show."
                : results.Count == 1 ? "1 matching broadcast." : $"{results.Count:N0} matching broadcasts.";
            return new MobileLibraryExploreQueryResult(facets, suggestions, results, status, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new MobileLibraryExploreQueryResult(
                null, [], [], "Explore failed: " + exception.Message, false);
        }
    }

    internal IReadOnlyList<MobileBroadcastItem> QueryCachedBroadcasts(
        int? collectionId,
        string searchText,
        string filter,
        int? year,
        int? month,
        bool hideCompleted,
        string? collectionName = null)
    {
        IEnumerable<WebClientLibraryBroadcastSummary> values = _cache.Snapshot.Broadcasts;
        if (!string.IsNullOrWhiteSpace(collectionName))
        {
            var collectionKey = NormalizeCollectionName(collectionName);
            values = values.Where(value => NormalizeCollectionName(value.CollectionName) == collectionKey);
        }
        else if (collectionId is > 0) values = values.Where(value => value.CollectionId == collectionId.Value);
        if (year is > 0) values = values.Where(value => value.AirDate?.Year == year.Value);
        if (month is > 0) values = values.Where(value => value.AirDate?.Month == month.Value);
        if (hideCompleted) values = values.Where(value => !value.Completed);
        values = filter.Trim().ToLowerInvariant() switch
        {
            "favourites" or "favorites" => values.Where(value => value.Favourite),
            "continuelistening" or "continue" => values.Where(value => value.InProgress && !value.Completed),
            "unplayed" => values.Where(value => !value.Completed && !value.InProgress),
            "completed" => values.Where(value => value.Completed),
            _ => values
        };
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var query = searchText.Trim();
            values = values.Where(value =>
                Contains(value.Title, query) ||
                Contains(value.Description, query) ||
                Contains(value.CollectionName, query) ||
                Contains(value.BroadcastId, query) ||
                Contains(value.SearchContext, query));
        }
        values = filter.Equals("RecentlyAdded", StringComparison.OrdinalIgnoreCase)
            ? values.OrderByDescending(value => value.DateAdded)
            : values.OrderByDescending(value => value.AirDate).ThenByDescending(value => value.DateAdded);
        return Convert(values);
    }

    internal IReadOnlyList<WebClientLibraryArchivePeriodSummary> BuildCachedArchivePeriods(
        int? collectionId,
        int? year,
        bool hideCompleted,
        string? collectionName = null)
    {
        IEnumerable<WebClientLibraryBroadcastSummary> source = _cache.Snapshot.Broadcasts;
        if (!string.IsNullOrWhiteSpace(collectionName))
        {
            var collectionKey = NormalizeCollectionName(collectionName);
            source = source.Where(value => NormalizeCollectionName(value.CollectionName) == collectionKey);
        }
        else if (collectionId is > 0) source = source.Where(value => value.CollectionId == collectionId.Value);
        if (hideCompleted) source = source.Where(value => !value.Completed);
        if (year is > 0) source = source.Where(value => value.AirDate?.Year == year.Value);
        var groups = year.HasValue
            ? source.Where(value => value.AirDate.HasValue).GroupBy(value => value.AirDate!.Value.Month)
            : source.Where(value => value.AirDate.HasValue).GroupBy(value => value.AirDate!.Value.Year);
        return groups
            .OrderByDescending(group => group.Key)
            .Select(group =>
            {
                var items = group.ToArray();
                var progress = items.Length == 0 ? 0 : (int)Math.Round(items.Average(value =>
                    value.Completed ? 100d : value.DurationMs <= 0 ? 0d :
                    Math.Clamp(value.PositionMs * 100d / value.DurationMs, 0d, 100d)));
                var title = year.HasValue
                    ? new DateTime(2000, group.Key, 1).ToString("MMMM")
                    : group.Key.ToString();
                return new WebClientLibraryArchivePeriodSummary(
                    group.Key,
                    title,
                    items.Length,
                    items.Count(value => value.Completed),
                    items.Count(value => value.Favourite),
                    progress,
                    $"{progress:N0}% listened",
                    $"{items.Select(value => value.CollectionName).Distinct(StringComparer.OrdinalIgnoreCase).Count():N0} show(s)",
                    items.Select(value => value.ArtworkPath)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)));
            })
            .ToArray();
    }

    internal static IReadOnlyList<WebClientLibraryCollectionSummary> CombineCollections(
        IEnumerable<WebClientLibraryCollectionSummary> values)
        => values
            .GroupBy(value => NormalizeCollectionName(value.CollectionName))
            .Where(group => group.Key.Length > 0)
            .Select(group =>
            {
                var distinct = group.GroupBy(value => value.CollectionId).Select(value => value.First()).ToArray();
                var display = distinct.OrderByDescending(value => value.BroadcastCount)
                    .ThenBy(value => value.CollectionName, StringComparer.CurrentCultureIgnoreCase)
                    .First();
                return new WebClientLibraryCollectionSummary(
                    display.CollectionId,
                    display.CollectionName.Trim(),
                    distinct.Sum(value => value.BroadcastCount));
            })
            .OrderBy(value => value.CollectionName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static MobileLibraryProjection FromOverview(WebClientLibraryOverview overview)
        => new(
            overview.TotalBroadcasts,
            overview.CompletedBroadcasts,
            overview.InProgressBroadcasts,
            overview.FavouriteBroadcasts,
            overview.Collections,
            overview.Collections,
            Convert(overview.ContinueListening),
            Convert(overview.RecentBroadcasts),
            Convert(overview.OnThisDay),
            null,
            null);

    private static IReadOnlyList<WebClientLibraryCollectionSummary> BuildCollections(
        IEnumerable<WebClientLibraryBroadcastSummary> broadcasts)
        => broadcasts
            .GroupBy(value => new { value.CollectionId, value.CollectionName })
            .Select(group => new WebClientLibraryCollectionSummary(
                group.Key.CollectionId,
                group.Key.CollectionName,
                group.Count()))
            .OrderBy(value => value.CollectionName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

    private static IReadOnlyList<int?> ResolveCollectionIds(
        int? collectionId,
        string? collectionName,
        IReadOnlyList<WebClientLibraryCollectionSummary> knownCollections)
    {
        if (string.IsNullOrWhiteSpace(collectionName)) return [collectionId];
        var key = NormalizeCollectionName(collectionName);
        var ids = knownCollections
            .Where(value => NormalizeCollectionName(value.CollectionName) == key)
            .Select(value => (int?)value.CollectionId)
            .Distinct()
            .ToArray();
        return ids.Length > 0 ? ids : [collectionId];
    }

    private static IReadOnlyList<WebClientLibraryArchivePeriodSummary> CombineArchivePeriods(
        IEnumerable<WebClientLibraryArchivePeriodSummary> values)
        => values
            .GroupBy(value => value.Value)
            .OrderByDescending(group => group.Key)
            .Select(group =>
            {
                var periods = group.ToArray();
                var broadcasts = periods.Sum(value => value.BroadcastCount);
                var progress = broadcasts == 0 ? 0 : (int)Math.Round(
                    periods.Sum(value => value.ProgressPercent * value.BroadcastCount) / (double)broadcasts);
                return new WebClientLibraryArchivePeriodSummary(
                    group.Key,
                    periods[0].Title,
                    broadcasts,
                    periods.Sum(value => value.CompletedCount),
                    periods.Sum(value => value.FavouriteCount),
                    progress,
                    $"{progress:N0}% listened",
                    periods[0].ShowsText,
                    periods.Select(value => value.ArtworkPath)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)));
            })
            .ToArray();

    private static MobileLibraryBroadcastQueryResult BroadcastResult(
        IReadOnlyList<MobileBroadcastItem> broadcasts,
        bool succeeded)
        => new(
            broadcasts,
            $"{broadcasts.Count:N0} broadcast{(broadcasts.Count == 1 ? string.Empty : "s")} shown",
            succeeded);

    private static MobileLibraryArchiveQueryResult ArchiveResult(
        IReadOnlyList<WebClientLibraryArchivePeriodSummary> periods,
        bool succeeded)
        => new(
            periods,
            $"{periods.Count:N0} archive period{(periods.Count == 1 ? string.Empty : "s")} shown",
            succeeded);

    private static IReadOnlyList<MobileBroadcastItem> Convert(
        IEnumerable<WebClientLibraryBroadcastSummary> values)
        => values.Select(value => new MobileBroadcastItem(value)).ToArray();

    private static string NormalizeCollectionName(string? value)
        => new((value ?? string.Empty).Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static bool Contains(string? value, string query)
        => value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true;
}

internal sealed record MobileLibraryProjection(
    int TotalBroadcasts,
    int CompletedBroadcasts,
    int InProgressBroadcasts,
    int FavouriteBroadcasts,
    IReadOnlyList<WebClientLibraryCollectionSummary> Collections,
    IReadOnlyList<WebClientLibraryCollectionSummary> IncompleteCollections,
    IReadOnlyList<MobileBroadcastItem> ContinueListening,
    IReadOnlyList<MobileBroadcastItem> RecentBroadcasts,
    IReadOnlyList<MobileBroadcastItem> OnThisDay,
    IReadOnlyList<MobileBroadcastItem>? UnheardBroadcasts,
    IReadOnlyList<MobileBroadcastItem>? InitialBroadcasts);

internal sealed record MobileLibraryBroadcastQueryResult(
    IReadOnlyList<MobileBroadcastItem> Broadcasts,
    string Status,
    bool Succeeded);

internal sealed record MobileLibraryArchiveQueryResult(
    IReadOnlyList<WebClientLibraryArchivePeriodSummary> Periods,
    string Status,
    bool Succeeded);

internal sealed record MobileLibraryExploreQueryResult(
    WebClientLibrarySearchFacets? Facets,
    IReadOnlyList<WebClientLibrarySearchSuggestion> Suggestions,
    IReadOnlyList<MobileBroadcastItem> Results,
    string Status,
    bool Succeeded);

internal interface IMobileLibraryQueryTransport
{
    Task<WebClientLibraryBrowseResult> BrowseAsync(
        string? searchText,
        int limit,
        int offset,
        int? collectionId,
        string filter,
        int? year,
        int? month,
        bool hideCompleted,
        string searchScope,
        bool hasTranscript,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WebClientLibraryArchivePeriodSummary>> GetArchivePeriodsAsync(
        int? collectionId,
        int? year,
        bool hideCompleted,
        CancellationToken cancellationToken = default);
    Task<WebClientLibrarySearchFacets> GetSearchFacetsAsync(
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<WebClientLibrarySearchSuggestion>> GetSearchSuggestionsAsync(
        string prefix,
        CancellationToken cancellationToken = default);
}

internal sealed class MobileLibraryQueryTransport(MobileServerClient server) : IMobileLibraryQueryTransport
{
    private readonly MobileServerClient _server = server ?? throw new ArgumentNullException(nameof(server));

    public Task<WebClientLibraryBrowseResult> BrowseAsync(
        string? searchText,
        int limit,
        int offset,
        int? collectionId,
        string filter,
        int? year,
        int? month,
        bool hideCompleted,
        string searchScope,
        bool hasTranscript,
        CancellationToken cancellationToken = default)
        => _server.BrowseAsync(
            searchText,
            limit,
            offset,
            collectionId,
            filter,
            year,
            month,
            hideCompleted,
            searchScope,
            hasTranscript,
            cancellationToken);

    public Task<IReadOnlyList<WebClientLibraryArchivePeriodSummary>> GetArchivePeriodsAsync(
        int? collectionId,
        int? year,
        bool hideCompleted,
        CancellationToken cancellationToken = default)
        => _server.GetArchivePeriodsAsync(collectionId, year, hideCompleted, cancellationToken);

    public Task<WebClientLibrarySearchFacets> GetSearchFacetsAsync(
        CancellationToken cancellationToken = default)
        => _server.GetSearchFacetsAsync(cancellationToken);

    public Task<IReadOnlyList<WebClientLibrarySearchSuggestion>> GetSearchSuggestionsAsync(
        string prefix,
        CancellationToken cancellationToken = default)
        => _server.GetSearchSuggestionsAsync(prefix, cancellationToken: cancellationToken);
}
