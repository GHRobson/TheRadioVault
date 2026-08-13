using TheRadioVault.Core.Domain;
using TheRadioVault.Services.Models;
using TheRadioVault.Services.Services;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

internal sealed partial class WebArchiveProvider
{
    public WebClientLibraryOverview GetClientLibraryOverview()
    {
        var value = CreateLibraryBrowseService().GetOverviewAsync().GetAwaiter().GetResult();
        return new WebClientLibraryOverview(
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

    public WebClientLibraryBroadcastSummary? GetClientLibraryBroadcast(long episodeId)
    {
        var value = CreateLibraryBrowseService().GetBroadcastAsync(episodeId).GetAwaiter().GetResult();
        return value is null ? null : Map(value);
    }

    public WebClientLibraryBrowseResult BrowseClientLibrary(WebClientLibraryBrowseRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var filter = Enum.TryParse<LibraryListeningFilter>(request.Filter, true, out var parsedFilter)
            ? parsedFilter
            : LibraryListeningFilter.All;
        var scope = Enum.TryParse<LibrarySearchScope>(request.SearchScope, true, out var parsedScope)
            ? parsedScope
            : LibrarySearchScope.All;
        var value = CreateLibraryBrowseService().BrowseAsync(new LibraryBrowseRequest(
            request.SearchText,
            request.CollectionId,
            filter,
            request.Year,
            request.Month,
            request.Limit,
            request.Offset,
            request.NewestFirst,
            scope,
            request.HasTranscript,
            request.HideCompleted)).GetAwaiter().GetResult();
        return new WebClientLibraryBrowseResult(
            value.Broadcasts.Select(Map).ToArray(),
            value.TotalMatching,
            value.UsesCanonicalLibrary);
    }

    public IReadOnlyList<WebClientLibraryArchivePeriodSummary> GetClientLibraryArchivePeriods(
        int? collectionId,
        int? year,
        bool hideCompleted)
        => CreateLibraryBrowseService()
            .GetArchivePeriodsAsync(collectionId, year, hideCompleted)
            .GetAwaiter()
            .GetResult()
            .Select(Map)
            .ToArray();

    public WebClientLibrarySearchFacets GetClientLibrarySearchFacets()
    {
        var value = CreateLibraryBrowseService().GetSearchFacetsAsync().GetAwaiter().GetResult();
        return new WebClientLibrarySearchFacets(value.Years, value.TranscriptCount);
    }

    public IReadOnlyList<WebClientLibrarySearchSuggestion> GetClientLibrarySearchSuggestions(string prefix, int limit)
        => CreateLibraryBrowseService()
            .GetSearchSuggestionsAsync(prefix, limit)
            .GetAwaiter()
            .GetResult()
            .Select(value => new WebClientLibrarySearchSuggestion(value.Value, value.Kind, value.MatchCount))
            .ToArray();

    public WebClientBroadcastDetails? GetClientBroadcastDetails(long episodeId)
    {
        var value = new BroadcastDetailsService(_database.PlatformDatabase)
            .GetAsync(episodeId)
            .GetAwaiter()
            .GetResult();
        return value is null ? null : new WebClientBroadcastDetails(
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
            BuildEntityLinks(value));
    }

    private LibraryBrowseService CreateLibraryBrowseService()
        => new(_database.PlatformDatabase);

    private static IReadOnlyList<ArchiveEntityLink> BuildEntityLinks(BroadcastDetails value)
    {
        var links = new List<ArchiveEntityLink>
        {
            ArchiveEntityLinkFactory.ForBroadcast(value.CanonicalKey, value.RepresentativeEpisodeId, value.Title),
            ArchiveEntityLinkFactory.ForShow(value.CollectionId, value.CollectionName)
        };
        links.AddRange(ArchiveEntityLinkFactory.ForDelimitedNames(value.Hosts, "host"));
        links.AddRange(ArchiveEntityLinkFactory.ForDelimitedNames(value.Guests, "guest"));
        links.AddRange(ArchiveEntityLinkFactory.ForDelimitedNames(value.Callers, "caller"));
        links.AddRange(ArchiveEntityLinkFactory.ForDelimitedNames(value.MentionedPeople, "mentioned"));
        links.AddRange(value.Topics
            .Where(topic => !string.IsNullOrWhiteSpace(topic))
            .Select(ArchiveEntityLinkFactory.ForTopic));
        return links
            .DistinctBy(link => (link.EntityKey, link.Relationship))
            .ToArray();
    }

    private static WebClientLibraryCollectionSummary Map(LibraryCollectionSummary value)
        => new(value.CollectionId, value.CollectionName, value.BroadcastCount);

    private static WebClientLibraryArchivePeriodSummary Map(LibraryArchivePeriodSummary value)
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

    private static WebClientLibraryBroadcastSummary Map(LibraryBroadcastSummary value)
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
            value.AttentionReason,
            value.SearchContext,
            value.SearchScore);
}
