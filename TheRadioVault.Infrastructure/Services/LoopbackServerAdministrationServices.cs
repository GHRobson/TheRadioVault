using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

public sealed class LoopbackServerLibraryFolderService : ILibraryFolderService
{
    private readonly LoopbackServerClient _connection;

    public LoopbackServerLibraryFolderService(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<IReadOnlyList<LibraryFolderRecord>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await _connection.SendJsonAsync<SettingsEnvelope>(
            HttpMethod.Get, WebApiRoutes.FederationSettings, cancellationToken: cancellationToken).ConfigureAwait(false);
        return envelope.Settings.ArchiveFolders.Select(folder => new LibraryFolderRecord(
            folder.Id,
            folder.Path,
            null,
            folder.CollectionName,
            folder.Recursive,
            true,
            folder.LastScanAt.HasValue ? new DateTimeOffset(folder.LastScanAt.Value) : null)).ToArray();
    }

    public Task<IReadOnlyList<LibraryFolderCollectionOption>> GetAssignableCollectionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<LibraryFolderCollectionOption>>(Array.Empty<LibraryFolderCollectionOption>());
    }

    public Task<long> AddAsync(string path, int? collectionId, bool recursive = true, CancellationToken cancellationToken = default)
        => Task.FromException<long>(ReadOnly());

    public Task SetCollectionAsync(long folderId, int? collectionId, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    public Task SetEnabledAsync(long folderId, bool enabled, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    public Task RemoveAsync(long folderId, CancellationToken cancellationToken = default)
        => Task.FromException(ReadOnly());

    private static InvalidOperationException ReadOnly()
        => new("Archive folders are managed in Radio Vault Server settings on the server computer.");

    private sealed record SettingsEnvelope(WebAuthoritativeSettingsSnapshot Settings);
}

public sealed class LoopbackServerArchiveHealthService : IArchiveHealthService
{
    private readonly LoopbackServerClient _connection;

    public LoopbackServerArchiveHealthService(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<ArchiveHealthReport> AnalyseAsync(
        ArchiveHealthOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var envelope = await _connection.SendJsonAsync<SettingsEnvelope>(
            HttpMethod.Get, WebApiRoutes.FederationSettings, cancellationToken: cancellationToken).ConfigureAwait(false);
        var settings = envelope.Settings;
        var health = settings.ArchiveHealth;
        return new ArchiveHealthReport(
            health.OverallScore,
            health.CollectionScore,
            health.MetadataScore,
            health.ResearchScore,
            health.PreservationScore,
            TotalBroadcasts: 0,
            RegisteredFolders: settings.ArchiveFolders.Count,
            MissingFiles: settings.Storage.Missing,
            CloudOnlyFiles: settings.Storage.CloudOnly,
            DuplicateCandidates: settings.Preservation.StrongDuplicateFilesAwaitingFullHash,
            NeedsReview: health.ActionableIssues,
            MissingArtwork: 0,
            GenericTitles: 0,
            UnfingerprintedFiles: settings.Preservation.MissingEvidence,
            NeverScannedFolders: settings.ArchiveFolders.Count(folder => !folder.LastScanAt.HasValue),
            TotalResearchRecords: health.ResearchAssessed ? 1 : 0,
            ConfirmedMissingBroadcasts: health.MissingBroadcasts,
            ProbableMissingBroadcasts: 0,
            UnknownResearchGaps: 0,
            ResearchNeedsReview: health.ResearchNeedsReview,
            ResearchConflicts: 0,
            UnsourcedResearchRecords: 0,
            LowConfidenceResearchRecords: 0,
            PendingReconciliationCandidates: health.PendingReconciliation,
            LastCompletedScanAt: health.LastCompletedScanAt,
            Issues: Array.Empty<ArchiveHealthIssue>())
        {
            ActionableIssueOverride = health.ActionableIssues
        };
    }

    private sealed record SettingsEnvelope(WebAuthoritativeSettingsSnapshot Settings);
}

public sealed class LoopbackServerLibraryMaintenanceService : ILibraryMaintenanceService
{
    private readonly LoopbackServerClient _connection;

    public LoopbackServerLibraryMaintenanceService(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public bool IsAvailable => _connection.IsAvailable;

    public async Task<LibraryMaintenanceSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
        => Map((await _connection.SendJsonAsync<ScanEnvelope>(
            HttpMethod.Get, WebApiRoutes.FederationLibraryScan, cancellationToken: cancellationToken).ConfigureAwait(false)).LibraryScan);

    public async Task<LibraryMaintenanceSnapshot> ScanAsync(CancellationToken cancellationToken = default)
        => Map((await _connection.SendJsonAsync<ScanEnvelope>(
            HttpMethod.Post,
            WebApiRoutes.FederationLibraryScan,
            new WebLibraryScanRequest("manual-native-client"),
            cancellationToken: cancellationToken).ConfigureAwait(false)).LibraryScan);

    private static LibraryMaintenanceSnapshot Map(WebLibraryScanSnapshot value)
        => new(
            value.IsRunning, value.Started, value.Trigger, value.StartedAt, value.CompletedAt,
            value.Message, value.FilesFound, value.Added, value.Updated, value.Unchanged,
            value.Errors, value.CanonicalBroadcastsAdded, value.CanonicalRecordingsAdded,
            value.CanonicalEpisodesMapped, value.CanonicalItemsNeedingReview);

    private sealed record ScanEnvelope(WebLibraryScanSnapshot LibraryScan);
}
