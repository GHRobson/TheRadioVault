using TheRadioVault.Core.Events;
using TheRadioVault.Core.Playback;
using TheRadioVault.Core.Services;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Jobs;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Services.Services;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

/// <summary>
/// UI-independent owner of the authoritative database and HTTP server. This is
/// the migration seam used by the dedicated settings-only server application.
/// </summary>
public sealed class RadioVaultServerRuntime : IDisposable
{
    private readonly SqliteDatabase _platformDatabase;
    private readonly DatabaseService _database;
    private readonly ApplicationEventBus _events;
    private readonly LivePlaybackStateStore _livePlayback;
    private readonly BackgroundJobQueue _jobs;
    private readonly ServerTranscriptionRuntime _transcription;
    private readonly HeadlessWebPlaybackController _playback;
    private readonly LocalWebServerService _server;
    private readonly ILibraryFolderService _libraryFolders;
    private readonly RssFeedIngestionService _rssFeeds;
    private readonly MediaConsolidationService _mediaConsolidation;
    private readonly ManagedArchiveRssCoordinator _managedArchiveRss;
    private readonly ArchiveReconciliationService _archiveReconciliation;
    private bool _disposed;

    public RadioVaultServerRuntime(
        string databasePath,
        WebServerPreferences? preferences = null,
        bool honorAutomaticStart = true)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("A database path is required.", nameof(databasePath));
        DatabasePath = Path.GetFullPath(databasePath);
        _platformDatabase = new SqliteDatabase(DatabasePath);
        _database = new DatabaseService(_platformDatabase);
        _database.Initialize();
        try
        {
            using var migrationConnection = _platformDatabase.OpenConnection();
            ResearchDateAuthoritySynchronizer.SynchronizeAsync(migrationConnection).GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            // The repair is idempotent and will be retried on the next launch.
            // A locked or partially restored Research ledger must not prevent
            // the server UI and playback service from starting.
            DiagnosticLog.Write("Research dates", "The legacy approved-date repair was deferred safely.", exception);
        }
        _events = new ApplicationEventBus();
        _livePlayback = new LivePlaybackStateStore();
        _jobs = new BackgroundJobQueue(2, _events);
        var dataDirectory = Path.GetDirectoryName(DatabasePath) ?? AppPaths.DataDirectory;
        _transcription = new ServerTranscriptionRuntime(_platformDatabase, _jobs, dataDirectory);
        _libraryFolders = new LibraryFolderService(_platformDatabase);
        _playback = new HeadlessWebPlaybackController();
        Preferences = preferences ?? WebServerPreferences.Load();
        _server = new LocalWebServerService(_database, Preferences, _events, _livePlayback, _jobs, _playback, _transcription);
        _rssFeeds = new RssFeedIngestionService(
            _platformDatabase,
            Preferences,
            async cancellationToken =>
            {
                var result = await _server.RunLibraryScanAsync("rss-feed-download", cancellationToken).ConfigureAwait(false);
                return result.Started && !result.IsRunning;
            });
        _mediaConsolidation = new MediaConsolidationService(_platformDatabase);
        _managedArchiveRss = new ManagedArchiveRssCoordinator(_platformDatabase);
        _archiveReconciliation = new ArchiveReconciliationService(_platformDatabase);
        try
        {
            var repair = _managedArchiveRss.Repair();
            if (repair.Configured) DiagnosticLog.Write("Managed archive", repair.Message);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("Managed archive", "The post-consolidation RSS repair was deferred safely.", exception);
        }
        _rssFeeds.Start();

        if (honorAutomaticStart && Preferences.Enabled && Preferences.StartAutomatically)
            Start();
    }

    public string DatabasePath { get; }
    public WebServerPreferences Preferences { get; private set; }
    public bool IsRunning => _server.IsRunning;
    public bool IsSecure => _server.IsSecure;
    public string? LastError => _server.LastError;
    public IReadOnlyList<string> AccessUrls => _server.IsRunning ? _server.GetAccessUrls() : Array.Empty<string>();
    public IReadOnlyList<string> SecureSetupUrls => _server.IsRunning && _server.IsSecure
        ? _server.GetSecureSetupUrls()
        : Array.Empty<string>();
    public ServerTranscriptionStatus TranscriptionStatus => _transcription.GetStatus();
    public ServerTranscriptionRuntime Transcription => _transcription;
    public IReadOnlyList<WebPairedDesktopClient> PairedDesktopClients => _server.PairedDesktopClients;
    public WebDesktopPairingSession? CurrentDesktopPairing => _server.CurrentDesktopPairing;
    public Task<IReadOnlyList<LibraryFolderRecord>> GetLibraryFoldersAsync(CancellationToken cancellationToken = default)
        => _libraryFolders.GetAllAsync(cancellationToken);
    public Task<IReadOnlyList<LibraryFolderCollectionOption>> GetAssignableLibraryCollectionsAsync(CancellationToken cancellationToken = default)
        => _libraryFolders.GetAssignableCollectionsAsync(cancellationToken);
    public Task<long> AddLibraryFolderAsync(string path, int? collectionId, bool recursive = true, CancellationToken cancellationToken = default)
        => _libraryFolders.AddAsync(path, collectionId, recursive, cancellationToken);
    public Task SetLibraryFolderCollectionAsync(long folderId, int? collectionId, CancellationToken cancellationToken = default)
        => _libraryFolders.SetCollectionAsync(folderId, collectionId, cancellationToken);
    public Task SetLibraryFolderEnabledAsync(long folderId, bool enabled, CancellationToken cancellationToken = default)
        => _libraryFolders.SetEnabledAsync(folderId, enabled, cancellationToken);
    public Task RemoveLibraryFolderAsync(long folderId, CancellationToken cancellationToken = default)
        => _libraryFolders.RemoveAsync(folderId, cancellationToken);
    public Task<WebLibraryScanSnapshot> ScanLibraryAsync(CancellationToken cancellationToken = default)
        => _server.RunLibraryScanAsync("manual-server-settings", cancellationToken);
    public Task<WebResearchPackPreviewResponse> PreviewKnowledgeDatabaseAsync(
        byte[] bytes,
        string sourceName,
        CancellationToken cancellationToken = default)
        => _server.PreviewResearchPackAsync(bytes, sourceName, cancellationToken);
    public Task<WebResearchPackPreviewResponse> PreviewKnowledgeDatabaseFileAsync(
        string filePath,
        string sourceName,
        CancellationToken cancellationToken = default)
        => _server.PreviewResearchPackFileAsync(filePath, sourceName, cancellationToken);
    public WebResearchPackImportJob StartKnowledgeDatabaseImport(Guid sessionId)
        => _server.StartResearchPackImport(sessionId);
    public WebResearchPackImportJob GetKnowledgeDatabaseImportStatus(Guid sessionId)
        => _server.GetResearchPackImportStatus(sessionId);
    public bool CancelKnowledgeDatabaseImport(Guid sessionId)
        => _server.CancelResearchPackImport(sessionId);
    public Task<WebResearchPackExportPayload> ExportKnowledgeDatabaseAsync(
        KnowledgeExportScope scope = KnowledgeExportScope.Complete,
        CancellationToken cancellationToken = default)
        => _server.ExportResearchPackAsync(scope, cancellationToken);
    public Task<IReadOnlyList<RssFeedSubscription>> GetRssFeedsAsync(CancellationToken cancellationToken = default)
        => _rssFeeds.GetAllAsync(cancellationToken);
    public Task<RssFeedSubscription> AddRssFeedAsync(RssFeedSaveRequest request, CancellationToken cancellationToken = default)
        => _rssFeeds.CreateAsync(request, cancellationToken);
    public Task SetRssFeedEnabledAsync(long feedId, bool enabled, CancellationToken cancellationToken = default)
        => _rssFeeds.SetEnabledAsync(feedId, enabled, cancellationToken);
    public Task DeleteRssFeedAsync(long feedId, CancellationToken cancellationToken = default)
        => _rssFeeds.DeleteAsync(feedId, cancellationToken);
    public Task<RssFeedCheckResult> CheckRssFeedsNowAsync(long? feedId = null, CancellationToken cancellationToken = default)
        => _rssFeeds.CheckNowAsync(feedId, cancellationToken);
    public ServerHealthSnapshot GetHealthSnapshot() => _server.GetHealthSnapshot();
    public BackupRestoreRehearsalResult RehearseLatestScheduledBackup()
        => _server.RehearseLatestScheduledBackup();
    public Task ExportRedactedDiagnosticsAsync(string destinationPath, CancellationToken cancellationToken = default)
        => new ServerHealthDiagnosticsService().ExportAsync(
            GetHealthSnapshot(), AppVersionService.Version, destinationPath, cancellationToken);
    public ArchiveReconciliationSnapshot GetArchiveReconciliationSnapshot()
        => _archiveReconciliation.GetSnapshot();
    public ArchiveReconciliationAudit GetArchiveReconciliationAudit(int detailLimit = 250)
        => _archiveReconciliation.GetAudit(detailLimit);
    public ArchiveReconciliationSnapshot ReconcileArchive(
        IProgress<(double Percent, string Message)>? progress = null,
        CancellationToken cancellationToken = default)
        => _archiveReconciliation.Reconcile(progress, cancellationToken);
    public void ExportArchiveReconciliationReport(string destinationPath)
        => _archiveReconciliation.ExportReport(destinationPath, AppVersionService.Version);
    public void ExportArchiveDateAuthorityEvidence(string destinationPath)
        => _archiveReconciliation.ExportDateAuthorityEvidence(destinationPath, AppVersionService.Version);
    public MediaConsolidationPlan PrepareMediaConsolidation(
        string managedRoot,
        string quarantineRoot,
        IProgress<MediaConsolidationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => _mediaConsolidation.CreatePlan(managedRoot, quarantineRoot, progress, cancellationToken);
    public MediaConsolidationPlan? LoadInterruptedMediaConsolidation(string quarantineRoot)
        => _mediaConsolidation.LoadLatestInterruptedPlan(quarantineRoot);
    public MediaConsolidationRehearsalResult RehearseMediaConsolidation(
        MediaConsolidationPlan plan,
        IProgress<MediaConsolidationProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => _mediaConsolidation.Rehearse(plan, progress, cancellationToken);
    public MediaConsolidationCommitResult CommitMediaConsolidation(
        MediaConsolidationPlan plan,
        MediaConsolidationRehearsalResult rehearsal,
        string confirmationText,
        IProgress<MediaConsolidationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (_server.IsRunning)
            throw new InvalidOperationException("Stop Radio Vault Server before committing media consolidation.");
        var result = _mediaConsolidation.Commit(plan, rehearsal, confirmationText, progress, cancellationToken);
        try
        {
            var repair = _managedArchiveRss.Repair(cancellationToken);
            return result with { Message = result.Message + " " + repair.Message };
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("Managed archive", "Consolidation completed, but RSS destination repair was deferred until the next server launch.", exception);
            return result with
            {
                Message = result.Message + " RSS destination repair will retry automatically the next time Radio Vault Server starts."
            };
        }
    }

    public WebDesktopPairingSession BeginDesktopPairing()
    {
        ThrowIfDisposed();
        return _server.BeginDesktopPairing();
    }

    public void CancelDesktopPairing()
    {
        ThrowIfDisposed();
        _server.CancelDesktopPairing();
    }

    public bool RevokeDesktopClient(string clientId)
    {
        ThrowIfDisposed();
        var removed = Preferences.RevokePairedDesktopClient(clientId);
        _server.RevokeDesktopClient(clientId);
        _server.UpdatePreferences(Preferences);
        return removed;
    }

    public int RevokeAllDesktopClients()
    {
        ThrowIfDisposed();
        var count = Preferences.PairedDesktopClients.Count;
        Preferences.RevokeAllPairedDesktopClients();
        _server.UpdatePreferences(Preferences);
        return count;
    }

    public void Start()
    {
        ThrowIfDisposed();
        if (_server.IsRunning) return;
        _server.Start();
    }

    public void Stop()
    {
        if (_disposed || !_server.IsRunning) return;
        _server.Stop();
    }

    public void Apply(WebServerPreferences preferences, bool shouldRun)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.Port == preferences.SecurePort)
            throw new InvalidOperationException("HTTP and HTTPS must use different ports.");

        if (_server.IsRunning) _server.Stop();
        preferences.Enabled = shouldRun;
        preferences.Save();
        Preferences = preferences;
        _server.UpdatePreferences(preferences);
        if (shouldRun) _server.Start();
    }

    public Task<SecureCertificateValidationResult> RunSecureDiagnosticsAsync()
    {
        ThrowIfDisposed();
        return _server.RunSecureDiagnosticsAsync();
    }

    public void RegenerateWebAccessToken()
    {
        ThrowIfDisposed();
        var shouldRun = _server.IsRunning;
        Preferences.RegenerateToken();
        Apply(Preferences, shouldRun);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rssFeeds.Dispose();
        _server.Dispose();
        _transcription.Dispose();
        _jobs.Dispose();
    }
}
