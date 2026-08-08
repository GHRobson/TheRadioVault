using System.Text.Json.Serialization;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile.Models;

public sealed record PairingEnvelope(WebDesktopPairingResult Result);
public sealed record BootstrapEnvelope(WebFederationBootstrap FederationBootstrap);
public sealed record OverviewEnvelope(WebClientLibraryOverview Overview);
public sealed record BrowseEnvelope(WebClientLibraryBrowseResult Result);

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
public partial class MobileJsonContext : JsonSerializerContext;
