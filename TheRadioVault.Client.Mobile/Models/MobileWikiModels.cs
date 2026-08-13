using TheRadioVault.Core.Domain;

namespace TheRadioVault.Client.Mobile.Models;

public sealed record MobileWikiOverview(
    int PageCount,
    int PublishedCount,
    int DraftCount,
    int SourceCount,
    int CitationCount,
    int ImageCount,
    int TimelineEventCount,
    DateTimeOffset? LastUpdatedAt,
    DateTimeOffset? LastImportedAt);

public sealed record MobileWikiBrowseRequest(
    string Search = "",
    string PageType = "",
    string Status = "",
    int Limit = 500);

public sealed record MobileWikiPageRequest(Guid PageId);

public sealed record MobileWikiDashboardRequest(int Month, int Day);

public sealed record MobileWikiPageSummary(
    Guid PageId,
    string Slug,
    string Title,
    string PageType,
    string Summary,
    string Status,
    int Revision,
    DateTimeOffset UpdatedAt,
    int CitationCount,
    int ImageCount,
    int TimelineEventCount)
{
    public string EvidenceSummary =>
        $"{CitationCount:N0} sources · {ImageCount:N0} images · {TimelineEventCount:N0} events";
}

public sealed record MobileWikiTimelineBroadcastLink(
    Guid EventId,
    long EpisodeId,
    long? MomentId,
    long? StartMs,
    long? EndMs,
    string Label,
    int SortOrder);

public sealed record MobileWikiTimelineEvent(
    Guid EventId,
    Guid PageId,
    string Title,
    string Summary,
    string Category,
    DateOnly? StartDate,
    DateOnly? EndDate,
    string DatePrecision,
    string DateDisplay,
    int Significance,
    int SortOrder,
    IReadOnlyList<Guid>? SourceIds = null,
    IReadOnlyList<Guid>? ImageIds = null,
    IReadOnlyList<MobileWikiTimelineBroadcastLink>? Broadcasts = null)
{
    public string YearText => StartDate?.Year.ToString() ?? DateDisplay;
    public string EvidenceSummary =>
        $"{(SourceIds?.Count ?? 0):N0} sources · {(ImageIds?.Count ?? 0):N0} images · {(Broadcasts?.Count ?? 0):N0} broadcasts";
}

public sealed record MobileWikiOnThisDayItem(
    MobileWikiPageSummary Page,
    MobileWikiTimelineEvent Event);

public sealed record MobileWikiEraSummary(
    int StartYear,
    int EndYear,
    int EventCount,
    int PageCount)
{
    public string Label => $"{StartYear}s";
    public string Summary => $"{EventCount:N0} events across {PageCount:N0} pages";
}

public sealed record MobileWikiDashboardHighlights(
    IReadOnlyList<MobileWikiOnThisDayItem> OnThisDay,
    IReadOnlyList<MobileWikiEraSummary> Eras);

public sealed record MobileWikiImageRecord(
    Guid ImageId,
    string OriginalFileName,
    string MediaType,
    long ByteCount,
    string Caption,
    string AltText,
    string Creator,
    string CopyrightHolder,
    string Licence,
    DateOnly? CapturedDate,
    DateOnly? RepresentativeFrom,
    DateOnly? RepresentativeTo,
    string DatePrecision,
    string DateNotes);

public sealed record MobileWikiPageImageLink(
    Guid PageId,
    Guid ImageId,
    string Role,
    int SortOrder,
    MobileWikiImageRecord? Image);

public sealed record MobileWikiImageRequest(Guid ImageId);

public sealed record MobileWikiImageContent(
    Guid ImageId,
    string MediaType,
    string FileName,
    byte[] Content);

public sealed record MobileWikiPageDocument(
    Guid PageId,
    string Slug,
    string Title,
    string PageType,
    string Summary,
    string BodyMarkdown,
    string Status,
    int Revision,
    DateTimeOffset UpdatedAt,
    string LastEditor,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<MobileWikiPageImageLink> Images,
    IReadOnlyList<MobileWikiTimelineEvent> Timeline,
    ArchiveEntityLink? EntityLink = null,
    IReadOnlyList<ArchiveEntityLink>? EntityLinks = null);

public sealed record MobileWikiOverviewEnvelope(MobileWikiOverview Value);
public sealed record MobileWikiBrowseEnvelope(IReadOnlyList<MobileWikiPageSummary> Value);
public sealed record MobileWikiHighlightsEnvelope(MobileWikiDashboardHighlights Value);
public sealed record MobileWikiPageEnvelope(MobileWikiPageDocument? Value);
public sealed record MobileWikiImageEnvelope(MobileWikiImageContent? Value);

public sealed record MobileExploreImage(
    Guid ImageId,
    Guid PageId,
    string PageTitle,
    string Caption,
    string AltText,
    string MediaType,
    byte[] Content);

public sealed record MobileExploreDashboard(
    MobileWikiOverview Overview,
    IReadOnlyList<MobileWikiPageSummary> AllPages,
    IReadOnlyList<MobileWikiPageSummary> FeaturedPages,
    IReadOnlyList<MobileWikiPageSummary> RecentPages,
    IReadOnlyList<MobileWikiPageSummary> ShowPages,
    IReadOnlyList<MobileWikiPageSummary> PeoplePages,
    IReadOnlyList<MobileWikiPageSummary> TopicPages,
    IReadOnlyList<MobileWikiPageSummary> TimelinePages,
    MobileWikiDashboardHighlights Highlights,
    IReadOnlyList<MobileExploreImage> Gallery);
