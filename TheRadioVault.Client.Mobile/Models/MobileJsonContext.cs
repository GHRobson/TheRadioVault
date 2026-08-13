using System.Text.Json.Serialization;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile.Models;

public sealed record PairingEnvelope(WebDesktopPairingResult Result);
public sealed record BootstrapEnvelope(WebFederationBootstrap FederationBootstrap);
public sealed record OverviewEnvelope(WebClientLibraryOverview Overview);
public sealed record BrowseEnvelope(WebClientLibraryBrowseResult Result);
public sealed record ArchivePeriodsEnvelope(IReadOnlyList<WebClientLibraryArchivePeriodSummary> Periods);
public sealed record SearchFacetsEnvelope(WebClientLibrarySearchFacets Facets);
public sealed record SearchSuggestionsEnvelope(IReadOnlyList<WebClientLibrarySearchSuggestion> Suggestions);
public sealed record SavedCollectionsEnvelope(IReadOnlyList<WebSavedCollectionSummary> Collections);
public sealed record SavedCollectionEnvelope(WebSavedCollectionDetails Collection);
public sealed record SavedCollectionMutationEnvelope(WebSavedCollectionMutationResult Result);
public sealed record BroadcastSummaryEnvelope(WebClientLibraryBroadcastSummary Broadcast);
public sealed record BroadcastDetailsEnvelope(WebClientBroadcastDetails Broadcast);
public sealed record MutationEnvelope(WebMutationResult Result);
public sealed record QueueMutationEnvelope(WebQueueMutationResult Result);
public sealed record QueueEnvelope(IReadOnlyList<WebQueueItem> Queue, int Count);
public sealed record PlaybackSessionEnvelope(WebPlaybackSession Session);
public sealed record ClientPlaybackEnvelope(WebClientPlaybackResult Result);
public sealed record PlaybackTransferEnvelope(WebPlaybackTransferResult Result);
public sealed record ProgressEnvelope(WebOfflineProgressResult Result);
public sealed record MomentMutationEnvelope(WebMomentMutationResult Result);
public sealed record MomentsEnvelope(IReadOnlyList<WebMomentSummary> Moments, int Count);
public sealed record MobileKnowledgeOverviewEnvelope(MobileKnowledgeOverview Value);
public sealed record MobileKnowledgeCollectionsEnvelope(IReadOnlyList<MobileKnowledgeCollection> Value);
public sealed record MobileKnowledgeDateReviewsEnvelope(IReadOnlyList<MobileKnowledgeDateReview> Value);
public sealed record MobileKnowledgeCoverageEnvelope(MobileKnowledgeCoverage? Value);
public sealed record MobileKnowledgeMutationEnvelope(bool Value);

[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RadioVaultMobileConnection))]
[JsonSerializable(typeof(WebLanDiscoveryAnnouncement))]
[JsonSerializable(typeof(WebDesktopPairingRequest))]
[JsonSerializable(typeof(PairingEnvelope))]
[JsonSerializable(typeof(BootstrapEnvelope))]
[JsonSerializable(typeof(OverviewEnvelope))]
[JsonSerializable(typeof(BrowseEnvelope))]
[JsonSerializable(typeof(ArchivePeriodsEnvelope))]
[JsonSerializable(typeof(SearchFacetsEnvelope))]
[JsonSerializable(typeof(SearchSuggestionsEnvelope))]
[JsonSerializable(typeof(SavedCollectionsEnvelope))]
[JsonSerializable(typeof(SavedCollectionEnvelope))]
[JsonSerializable(typeof(SavedCollectionMutationEnvelope))]
[JsonSerializable(typeof(WebSavedCollectionCreateRequest))]
[JsonSerializable(typeof(WebSavedCollectionUpdateRequest))]
[JsonSerializable(typeof(WebSavedCollectionItemMutation))]
[JsonSerializable(typeof(WebSavedCollectionDeleteRequest))]
[JsonSerializable(typeof(BroadcastSummaryEnvelope))]
[JsonSerializable(typeof(BroadcastDetailsEnvelope))]
[JsonSerializable(typeof(MutationEnvelope))]
[JsonSerializable(typeof(QueueMutationEnvelope))]
[JsonSerializable(typeof(QueueEnvelope))]
[JsonSerializable(typeof(MobileFavouriteMutation))]
[JsonSerializable(typeof(MobileListeningStatusMutation))]
[JsonSerializable(typeof(MobileQueueAddMutation))]
[JsonSerializable(typeof(MobileQueueMoveMutation))]
[JsonSerializable(typeof(MobileEmptyMutation))]
[JsonSerializable(typeof(MobileDownloadIndex))]
[JsonSerializable(typeof(MobileOfflineMutationIndex))]
[JsonSerializable(typeof(MobileOfflineMutation))]
[JsonSerializable(typeof(WebCanonicalMediaManifest))]
[JsonSerializable(typeof(PlaybackSessionEnvelope))]
[JsonSerializable(typeof(ClientPlaybackEnvelope))]
[JsonSerializable(typeof(PlaybackTransferEnvelope))]
[JsonSerializable(typeof(ProgressEnvelope))]
[JsonSerializable(typeof(MomentMutationEnvelope))]
[JsonSerializable(typeof(MomentsEnvelope))]
[JsonSerializable(typeof(WebMomentMutation))]
[JsonSerializable(typeof(MobileLibrarySyncEnvelope))]
[JsonSerializable(typeof(MobileMetadataCacheSnapshot))]
[JsonSerializable(typeof(WebClientPlaybackUpdate))]
[JsonSerializable(typeof(WebPlaybackTransferBeginRequest))]
[JsonSerializable(typeof(WebPlaybackTransferReadyRequest))]
[JsonSerializable(typeof(WebPlaybackTransferCommitRequest))]
[JsonSerializable(typeof(WebPlaybackTransferCancelRequest))]
[JsonSerializable(typeof(WebPlaybackTransferSourceStoppedRequest))]
[JsonSerializable(typeof(WebOfflineProgressUpdate))]
[JsonSerializable(typeof(MobileWikiOverviewEnvelope))]
[JsonSerializable(typeof(MobileWikiBrowseEnvelope))]
[JsonSerializable(typeof(MobileWikiHighlightsEnvelope))]
[JsonSerializable(typeof(MobileWikiPageEnvelope))]
[JsonSerializable(typeof(MobileWikiImageEnvelope))]
[JsonSerializable(typeof(MobileWikiBrowseRequest))]
[JsonSerializable(typeof(MobileWikiPageRequest))]
[JsonSerializable(typeof(MobileWikiImageRequest))]
[JsonSerializable(typeof(MobileWikiDashboardRequest))]
[JsonSerializable(typeof(MobileKnowledgeOverviewEnvelope))]
[JsonSerializable(typeof(MobileKnowledgeCollectionsEnvelope))]
[JsonSerializable(typeof(MobileKnowledgeDateReviewsEnvelope))]
[JsonSerializable(typeof(MobileKnowledgeCoverageEnvelope))]
[JsonSerializable(typeof(MobileKnowledgeMutationEnvelope))]
[JsonSerializable(typeof(MobileKnowledgeDateReviewsRequest))]
[JsonSerializable(typeof(MobileKnowledgeCollectionRequest))]
[JsonSerializable(typeof(MobileKnowledgeResolveRequest))]
public partial class MobileJsonContext : JsonSerializerContext;
