using System.Globalization;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

public sealed class LoopbackLibraryBrowseService : ILibraryBrowseService
{
    private readonly LoopbackServerClient _connection;

    public LoopbackLibraryBrowseService(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<LibraryOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await _connection.SendJsonAsync<OverviewEnvelope>(HttpMethod.Get, WebApiRoutes.ClientLibraryOverview, cancellationToken: cancellationToken).ConfigureAwait(false);
        var value = envelope.Overview;
        return new LibraryOverview(
            value.TotalBroadcasts,
            value.CompletedBroadcasts,
            value.InProgressBroadcasts,
            value.FavouriteBroadcasts,
            value.NeedsAttentionBroadcasts,
            value.UsesCanonicalLibrary,
            value.Collections.Select(Map).ToArray(),
            value.ContinueListening.Select(Map).ToArray(),
            value.RecentBroadcasts.Select(Map).ToArray(),
            value.OnThisDay.Select(Map).ToArray());
    }

    public async Task<LibraryBroadcastSummary?> GetBroadcastAsync(long representativeEpisodeId, CancellationToken cancellationToken = default)
    {
        if (representativeEpisodeId <= 0) return null;
        var envelope = await _connection.GetJsonOrNullAsync<BroadcastEnvelope>(
            WebApiRoutes.ClientLibraryBroadcast(representativeEpisodeId), cancellationToken).ConfigureAwait(false);
        return envelope is null ? null : Map(envelope.Broadcast);
    }

    public async Task<LibraryBrowseResult> BrowseAsync(LibraryBrowseRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var query = BuildQuery(new Dictionary<string, string?>
        {
            ["q"] = request.SearchText,
            ["collectionId"] = Format(request.CollectionId),
            ["filter"] = request.Filter.ToString(),
            ["year"] = Format(request.Year),
            ["month"] = Format(request.Month),
            ["limit"] = request.Limit.ToString(CultureInfo.InvariantCulture),
            ["offset"] = request.Offset.ToString(CultureInfo.InvariantCulture),
            ["newestFirst"] = request.NewestFirst.ToString(CultureInfo.InvariantCulture),
            ["scope"] = request.SearchScope.ToString(),
            ["hasTranscript"] = request.HasTranscript.ToString(CultureInfo.InvariantCulture)
        });
        var envelope = await _connection.SendJsonAsync<BrowseEnvelope>(
            HttpMethod.Get, WebApiRoutes.ClientLibraryBrowse + query, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new LibraryBrowseResult(
            envelope.Result.Broadcasts.Select(Map).ToArray(),
            envelope.Result.TotalMatching,
            envelope.Result.UsesCanonicalLibrary);
    }

    public async Task<IReadOnlyList<LibraryArchivePeriodSummary>> GetArchivePeriodsAsync(
        int? collectionId,
        int? year,
        bool hideCompleted = false,
        CancellationToken cancellationToken = default)
    {
        var query = BuildQuery(new Dictionary<string, string?>
        {
            ["collectionId"] = Format(collectionId),
            ["year"] = Format(year),
            ["hideCompleted"] = hideCompleted.ToString(CultureInfo.InvariantCulture)
        });
        var envelope = await _connection.SendJsonAsync<PeriodsEnvelope>(
            HttpMethod.Get, WebApiRoutes.ClientLibraryArchivePeriods + query, cancellationToken: cancellationToken).ConfigureAwait(false);
        return envelope.Periods.Select(Map).ToArray();
    }

    public async Task<LibrarySearchFacets> GetSearchFacetsAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await _connection.SendJsonAsync<FacetsEnvelope>(
            HttpMethod.Get, WebApiRoutes.ClientLibrarySearchFacets, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new LibrarySearchFacets(envelope.Facets.Years, envelope.Facets.TranscriptCount);
    }

    public async Task<IReadOnlyList<LibrarySearchSuggestion>> GetSearchSuggestionsAsync(
        string prefix,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var query = BuildQuery(new Dictionary<string, string?>
        {
            ["prefix"] = prefix,
            ["limit"] = limit.ToString(CultureInfo.InvariantCulture)
        });
        var envelope = await _connection.SendJsonAsync<SuggestionsEnvelope>(
            HttpMethod.Get, WebApiRoutes.ClientLibrarySearchSuggestions + query, cancellationToken: cancellationToken).ConfigureAwait(false);
        return envelope.Suggestions.Select(value => new LibrarySearchSuggestion(value.Value, value.Kind, value.MatchCount)).ToArray();
    }

    internal static LibraryBroadcastSummary Map(WebClientLibraryBroadcastSummary value)
        => new(
            value.CanonicalKey,
            value.RepresentativeEpisodeId,
            value.BroadcastId,
            value.CollectionId,
            value.CollectionName,
            value.AirDate,
            value.DateAdded,
            value.BroadcastSlot,
            value.Title,
            value.Description,
            value.Favourite,
            value.Completed,
            value.InProgress,
            value.PositionMs,
            value.DurationMs,
            value.LastPlayedAt,
            value.ArtworkPath,
            value.RecordingCount,
            value.SegmentCount,
            value.PhysicalFileCount,
            value.NeedsAttention,
            value.AttentionReason)
        {
            SearchContext = value.SearchContext,
            SearchScore = value.SearchScore
        };

    private static LibraryCollectionSummary Map(WebClientLibraryCollectionSummary value)
        => new(value.CollectionId, value.CollectionName, value.BroadcastCount);

    private static LibraryArchivePeriodSummary Map(WebClientLibraryArchivePeriodSummary value)
        => new(
            value.Value,
            value.Title,
            value.BroadcastCount,
            value.CompletedCount,
            value.FavouriteCount,
            value.ProgressPercent,
            value.ProgressText,
            value.ShowsText,
            value.ArtworkPath);

    private static string BuildQuery(IReadOnlyDictionary<string, string?> values)
        => "?" + string.Join("&", values
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
            .Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value!)));

    private static string? Format(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private sealed record OverviewEnvelope(WebClientLibraryOverview Overview);
    private sealed record BroadcastEnvelope(WebClientLibraryBroadcastSummary Broadcast);
    private sealed record BrowseEnvelope(WebClientLibraryBrowseResult Result);
    private sealed record PeriodsEnvelope(IReadOnlyList<WebClientLibraryArchivePeriodSummary> Periods);
    private sealed record FacetsEnvelope(WebClientLibrarySearchFacets Facets);
    private sealed record SuggestionsEnvelope(IReadOnlyList<WebClientLibrarySearchSuggestion> Suggestions);
}

public sealed class LoopbackBroadcastDetailsService : IBroadcastDetailsService
{
    private readonly LoopbackServerClient _connection;

    public LoopbackBroadcastDetailsService(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<BroadcastDetails?> GetAsync(long representativeEpisodeId, CancellationToken cancellationToken = default)
    {
        if (representativeEpisodeId <= 0) return null;
        var envelope = await _connection.GetJsonOrNullAsync<DetailsEnvelope>(
            WebApiRoutes.ClientBroadcast(representativeEpisodeId), cancellationToken).ConfigureAwait(false);
        if (envelope is null) return null;
        var value = envelope.Broadcast;
        return new BroadcastDetails(
            value.RepresentativeEpisodeId,
            value.CanonicalKey,
            value.BroadcastId,
            value.CollectionId,
            value.CollectionName,
            value.AirDate,
            value.Slot,
            value.Title,
            value.Summary,
            value.Station,
            value.Edition,
            value.BroadcastVariant,
            value.BroadcastEra,
            value.EpisodeType,
            value.ArchiveNotes,
            value.CatalogueSeries,
            value.CatalogueProgramme,
            value.CatalogueFormat,
            value.OriginalReleaseDate,
            value.RecordingDate,
            value.Venue,
            value.Event,
            value.Network,
            value.CatalogueNumber,
            value.OriginalFilename,
            value.Provenance,
            value.ResearchNotes,
            value.PersonalNotes,
            value.Hosts,
            value.Guests,
            value.Callers,
            value.MentionedPeople,
            value.Topics,
            value.ArtworkPath,
            value.RecordingCount,
            value.SegmentCount,
            value.PhysicalFileCount,
            IsRemoteOwned: true);
    }

    private sealed record DetailsEnvelope(WebClientBroadcastDetails Broadcast);
}
