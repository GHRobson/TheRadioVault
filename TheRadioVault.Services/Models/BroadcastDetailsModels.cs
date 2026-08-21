using TheRadioVault.Core.Domain;

namespace TheRadioVault.Services.Models;

public sealed record BroadcastMetadataField(string Label, string Value);

public sealed record BroadcastDetails(
    long RepresentativeEpisodeId,
    string CanonicalKey,
    string BroadcastId,
    int CollectionId,
    string CollectionName,
    DateOnly? AirDate,
    string Slot,
    string Title,
    string Summary,
    string Station,
    string Edition,
    string BroadcastVariant,
    string BroadcastEra,
    string EpisodeType,
    string ArchiveNotes,
    string CatalogueSeries,
    string CatalogueProgramme,
    string CatalogueFormat,
    string OriginalReleaseDate,
    string RecordingDate,
    string Venue,
    string Event,
    string Network,
    string CatalogueNumber,
    string OriginalFilename,
    string Provenance,
    string ResearchNotes,
    string PersonalNotes,
    string Hosts,
    string Guests,
    string Callers,
    string MentionedPeople,
    IReadOnlyList<string> Topics,
    string? ArtworkPath,
    int RecordingCount,
    int SegmentCount,
    int PhysicalFileCount,
    bool IsRemoteOwned = false,
    IReadOnlyList<ArchiveEntityLink>? EntityLinks = null)
{
    public string DateText => AirDate?.ToString("dddd, d MMMM yyyy")
        ?? (!string.IsNullOrWhiteSpace(OriginalReleaseDate)
            ? TheRadioVault.Core.Services.CatalogueDateService.FormatForDisplay(OriginalReleaseDate)
            : "Date unknown");
    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool HasArchiveNotes => !string.IsNullOrWhiteSpace(ArchiveNotes);
    public bool HasPersonalNotes => !string.IsNullOrWhiteSpace(PersonalNotes);
    public bool HasHosts => !string.IsNullOrWhiteSpace(Hosts);
    public bool HasGuests => !string.IsNullOrWhiteSpace(Guests);
    public bool HasCallers => !string.IsNullOrWhiteSpace(Callers);
    public bool HasMentionedPeople => !string.IsNullOrWhiteSpace(MentionedPeople);
    public bool HasPeople => HasHosts || HasGuests || HasCallers || HasMentionedPeople;
    public bool HasTopics => Topics.Count > 0;
    public bool HasArtwork => !string.IsNullOrWhiteSpace(ArtworkPath) && File.Exists(ArtworkPath);
    public IReadOnlyList<BroadcastMetadataField> CatalogueFields => new BroadcastMetadataField[]
    {
        new("Series", CatalogueSeries),
        new("Programme", CatalogueProgramme),
        new("Format", CatalogueFormat),
        new("Original release", OriginalReleaseDate),
        new("Recorded", RecordingDate),
        new("Venue", Venue),
        new("Event", Event),
        new("Network / platform", Network),
        new("Catalogue number", CatalogueNumber),
        new("Original filename", OriginalFilename),
        new("Provenance", Provenance),
        new("Research notes", ResearchNotes)
    }.Where(field => !string.IsNullOrWhiteSpace(field.Value)).ToArray();
    public bool HasCatalogueDetails => CatalogueFields.Count > 0;
    public IReadOnlyList<ArchiveEntityLink> AllEntityLinks => EntityLinks is { Count: > 0 }
        ? EntityLinks
        : BuildFallbackEntityLinks();
    public IReadOnlyList<ArchiveEntityLink> HostLinks => LinksForRelationship("host");
    public IReadOnlyList<ArchiveEntityLink> GuestLinks => LinksForRelationship("guest");
    public IReadOnlyList<ArchiveEntityLink> CallerLinks => LinksForRelationship("caller");
    public IReadOnlyList<ArchiveEntityLink> MentionedPeopleLinks => LinksForRelationship("mentioned");
    public IReadOnlyList<ArchiveEntityLink> TopicLinks => AllEntityLinks
        .Where(link => link.Kind == ArchiveEntityKind.Topic)
        .DistinctBy(link => link.EntityKey)
        .ToArray();
    public string CatalogueContextText => string.Join(" · ", CatalogueFields
        .Where(field => field.Label is "Programme" or "Format" or "Original release" or "Venue")
        .Take(3)
        .Select(field => field.Value));
    public string MediaStructureText => SegmentCount > 1
        ? $"{SegmentCount:N0} parts · {RecordingCount:N0} recording{(RecordingCount == 1 ? string.Empty : "s")} · {PhysicalFileCount:N0} file{(PhysicalFileCount == 1 ? string.Empty : "s")}" 
        : RecordingCount > 1
            ? $"{RecordingCount:N0} recordings · {PhysicalFileCount:N0} files"
            : $"Single recording · {PhysicalFileCount:N0} file{(PhysicalFileCount == 1 ? string.Empty : "s")}";
    public string TechnicalIdentityText => string.Join(" · ", new[]
    {
        string.IsNullOrWhiteSpace(CanonicalKey) ? null : CanonicalKey,
        string.IsNullOrWhiteSpace(BroadcastId) ? null : BroadcastId,
        IsRemoteOwned ? "Server-owned" : "Local authoritative library"
    }.Where(x => !string.IsNullOrWhiteSpace(x)));

    private IReadOnlyList<ArchiveEntityLink> LinksForRelationship(string relationship)
        => AllEntityLinks
            .Where(link => link.Kind == ArchiveEntityKind.Person &&
                           string.Equals(link.Relationship, relationship, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(link => link.EntityKey)
            .ToArray();

    private IReadOnlyList<ArchiveEntityLink> BuildFallbackEntityLinks()
    {
        var links = new List<ArchiveEntityLink>
        {
            ArchiveEntityLinkFactory.ForBroadcast(CanonicalKey, RepresentativeEpisodeId, Title),
            ArchiveEntityLinkFactory.ForShow(CollectionId, CollectionName)
        };
        links.AddRange(ArchiveEntityLinkFactory.ForDelimitedNames(Hosts, "host"));
        links.AddRange(ArchiveEntityLinkFactory.ForDelimitedNames(Guests, "guest"));
        links.AddRange(ArchiveEntityLinkFactory.ForDelimitedNames(Callers, "caller"));
        links.AddRange(ArchiveEntityLinkFactory.ForDelimitedNames(MentionedPeople, "mentioned"));
        links.AddRange(Topics.Where(topic => !string.IsNullOrWhiteSpace(topic)).Select(ArchiveEntityLinkFactory.ForTopic));
        return links.DistinctBy(link => (link.EntityKey, link.Relationship)).ToArray();
    }
}
