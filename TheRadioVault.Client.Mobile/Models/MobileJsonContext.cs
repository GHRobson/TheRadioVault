using System.Text.Json.Serialization;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile.Models;

public sealed record PairingEnvelope(WebDesktopPairingResult Result);
public sealed record BootstrapEnvelope(WebFederationBootstrap FederationBootstrap);
public sealed record OverviewEnvelope(WebClientLibraryOverview Overview);
public sealed record BrowseEnvelope(WebClientLibraryBrowseResult Result);
public sealed record PlaybackSessionEnvelope(WebPlaybackSession Session);
public sealed record ClientPlaybackEnvelope(WebClientPlaybackResult Result);
public sealed record PlaybackTransferEnvelope(WebPlaybackTransferResult Result);
public sealed record ProgressEnvelope(WebOfflineProgressResult Result);

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
[JsonSerializable(typeof(WebCanonicalMediaManifest))]
[JsonSerializable(typeof(PlaybackSessionEnvelope))]
[JsonSerializable(typeof(ClientPlaybackEnvelope))]
[JsonSerializable(typeof(PlaybackTransferEnvelope))]
[JsonSerializable(typeof(ProgressEnvelope))]
[JsonSerializable(typeof(WebClientPlaybackUpdate))]
[JsonSerializable(typeof(WebPlaybackTransferBeginRequest))]
[JsonSerializable(typeof(WebPlaybackTransferReadyRequest))]
[JsonSerializable(typeof(WebPlaybackTransferCommitRequest))]
[JsonSerializable(typeof(WebPlaybackTransferCancelRequest))]
[JsonSerializable(typeof(WebPlaybackTransferSourceStoppedRequest))]
[JsonSerializable(typeof(WebOfflineProgressUpdate))]
public partial class MobileJsonContext : JsonSerializerContext;
