using System.Globalization;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

/// <summary>
/// Compares the encrypted native-client cache with the server's bounded change
/// journal. The metadata-only request never downloads a second full Library;
/// presentation refreshes only the affected cached projections.
/// </summary>
public sealed class NativeClientCacheSyncService
{
    private readonly LoopbackServerClient _connection;
    private readonly NativeServerConnectionPreferences _preferences;

    public NativeClientCacheSyncService(
        LoopbackServerClient connection,
        NativeServerConnectionPreferences preferences)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _preferences = preferences ?? throw new ArgumentNullException(nameof(preferences));
    }

    public async Task<NativeClientCacheSyncPlan> CheckAsync(CancellationToken cancellationToken = default)
    {
        var hasCursor = !string.IsNullOrWhiteSpace(_preferences.LibrarySyncSessionId) &&
                        _preferences.LibrarySyncSequence >= 0 &&
                        !string.IsNullOrWhiteSpace(_preferences.LibrarySyncRevision);
        var path = WebApiRoutes.FederationLibrarySync +
                   "?after=" + (hasCursor ? _preferences.LibrarySyncSequence : 0).ToString(CultureInfo.InvariantCulture) +
                   "&session=" + Uri.EscapeDataString(hasCursor ? _preferences.LibrarySyncSessionId : string.Empty) +
                   "&revision=" + Uri.EscapeDataString(hasCursor ? _preferences.LibrarySyncRevision : string.Empty) +
                   "&metadataOnly=true";
        var envelope = await _connection.GetLiveJsonAsync<SyncEnvelope>(path, cancellationToken).ConfigureAwait(false);
        var sync = envelope.Sync;
        if (!string.Equals(sync.ServerInstanceId, _preferences.ServerInstanceId, StringComparison.Ordinal))
            throw new InvalidOperationException("The cache refresh was answered by a different Radio Vault Server.");

        var kinds = sync.Changes
            .Select(change => (change.Kind ?? string.Empty).Trim())
            .Where(kind => kind.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new NativeClientCacheSyncPlan(
            RequiresFullRefresh: !hasCursor || sync.ResetRequired,
            NoChanges: hasCursor && sync.NoChanges,
            Kinds: kinds,
            SessionId: sync.SessionId,
            Sequence: sync.Sequence,
            Revision: sync.LibraryRevision,
            GeneratedAt: sync.GeneratedAt);
    }

    public void Commit(NativeClientCacheSyncPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _preferences.LibrarySyncSessionId = plan.SessionId;
        _preferences.LibrarySyncSequence = Math.Max(0, plan.Sequence);
        _preferences.LibrarySyncRevision = plan.Revision;
        _preferences.LibraryCacheSynchronizedAt = DateTimeOffset.UtcNow;
        _preferences.Save();
    }

    private sealed record SyncEnvelope(WebFederationLibrarySync Sync);
}

public sealed record NativeClientCacheSyncPlan(
    bool RequiresFullRefresh,
    bool NoChanges,
    IReadOnlySet<string> Kinds,
    string SessionId,
    long Sequence,
    string Revision,
    DateTimeOffset GeneratedAt);
