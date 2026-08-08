using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Input;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;
using TheRadioVault.Transcription.Services;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class SettingsSectionItemViewModel : ObservableObject
{
    private bool _isSelected;

    public SettingsSectionItemViewModel(string key, string title, string description, Action<SettingsSectionItemViewModel> select)
    {
        Key = key;
        Title = title;
        Description = description;
        SelectCommand = new DelegateCommand(() => select(this));
    }

    public string Key { get; }
    public string Title { get; }
    public string Description { get; }
    public ICommand SelectCommand { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed class DesktopToolsViewModel : ObservableObject, IDisposable
{
    private static readonly int[] PlaybackIntervals = { 10, 15, 30, 60 };
    private static readonly int[] CompletionThresholds = { 1, 2, 5, 10, 30, 60 };
    private readonly ILibraryFolderService? _folders;
    private readonly IArchiveHealthService? _health;
    private readonly IArchiveBackupService? _backup;
    private readonly ILibraryMaintenanceService _libraryMaintenance;
    private readonly IRadioVaultAnywhereService _anywhere;
    private readonly IUiDispatcher _dispatcher;
    private readonly IConnectedPlaybackDiagnosticsService _connectedDiagnostics;
    private readonly IFileSelectionService _files;
    private readonly ILibraryFolderShowSelectionService _folderShowSelection;
    private readonly IClipboardService _clipboard;
    private readonly IExternalLauncherService _launcher;
    private readonly PlaybackViewModel _playback;
    private readonly string _dataDirectory;
    private readonly string _diagnosticLogPath;
    private readonly IAppThemeService _theme;
    private readonly IServerTranscriptionAdministrationService _transcriptionAdministration;
    private bool _isBusy;
    private bool _isLoaded;
    private string _statusText;
    private ArchiveHealthReport? _healthReport;
    private LibraryMaintenanceSnapshot _libraryScan = new(
        false, false, string.Empty, null, null,
        "Library scan status has not loaded.",
        0, 0, 0, 0, 0, 0, 0, 0, 0);
    private LibraryFolderRecord? _selectedFolder;
    private SettingsSectionItemViewModel? _selectedSection;
    private int _skipBackSeconds = 30;
    private int _skipForwardSeconds = 30;
    private int _completionThresholdSeconds = 5;
    private bool _confirmResetToUnplayed = true;
    private string _transcriptionExecutablePath = string.Empty;
    private string _transcriptionModelPath = string.Empty;
    private string _transcriptionVadModelPath = string.Empty;
    private string _diarizationSegmentationModelPath = string.Empty;
    private string _diarizationEmbeddingModelPath = string.Empty;
    private string _transcriptionLanguage = "auto";
    private int _transcriptionThreads;
    private bool _transcriptionUseGpu = true;
    private bool _transcriptionUseVad;
    private bool _transcriptionMultiSpeakerDiarization;
    private bool _transcriptionUseArchiveContext = true;
    private WhisperModelCatalogItem _selectedTranscriptionModel = WhisperModelCatalog.Items.First(x => x.Id == "base.en");
    private ServerTranscriptionAdministrationStatus _serverTranscriptionStatus = new(
        false, "Checking the active server's transcription service…", "", "", "", false, "", false);
    private bool _isTranscriptionDownloadActive;
    private double? _transcriptionDownloadPercent;
    private CancellationTokenSource? _transcriptionDownloadCancellation;
    private RadioVaultAnywhereSnapshot _anywhereSnapshot;
    private bool _anywhereEnabled;
    private bool _anywhereStartAutomatically;
    private string _anywhereServerName = string.Empty;
    private int _anywhereHttpPort = 8765;
    private bool _anywhereSecureAccess = true;
    private int _anywhereHttpsPort = 8766;
    private int _anywhereDiscoveryPort = 30829;
    private RadioVaultAnywhereClient? _selectedPairedClient;
    private PhoneQrCode _anywhereQrCode = PhoneQrCode.Empty;
    private PhoneQrCode _anywhereSetupQrCode = PhoneQrCode.Empty;
    private string _versionText;
    private CancellationTokenSource? _diagnosticCancellation;
    private ConnectedPlaybackDiagnosticReport? _diagnosticReport;
    private bool _isDiagnosticsRunning;
    private string _diagnosticSessionCode = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private string _diagnosticSummary = "Run a quick or full connected-playback test to create a privacy-safe report.";

    public event EventHandler? LibraryScanCompleted;

    public DesktopToolsViewModel(
        ILibraryFolderService? folders,
        IArchiveHealthService? health,
        IArchiveBackupService? backup,
        ILibraryMaintenanceService libraryMaintenance,
        IRadioVaultAnywhereService anywhere,
        IUiDispatcher dispatcher,
        IConnectedAccessService connectedAccess,
        IConnectedPlaybackDiagnosticsService connectedDiagnostics,
        IFileSelectionService files,
        ILibraryFolderShowSelectionService folderShowSelection,
        IClipboardService clipboard,
        IExternalLauncherService launcher,
        PlaybackViewModel playback,
        IAppThemeService theme,
        IServerTranscriptionAdministrationService transcriptionAdministration,
        string dataDirectory,
        string diagnosticLogPath,
        bool isRemoteSession,
        string versionText)
    {
        _folders = folders;
        _health = health;
        _backup = backup;
        _libraryMaintenance = libraryMaintenance ?? throw new ArgumentNullException(nameof(libraryMaintenance));
        _anywhere = anywhere ?? throw new ArgumentNullException(nameof(anywhere));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ConnectedAccess = new ConnectedAccessViewModel(
            connectedAccess ?? throw new ArgumentNullException(nameof(connectedAccess)),
            _dispatcher);
        _connectedDiagnostics = connectedDiagnostics ?? throw new ArgumentNullException(nameof(connectedDiagnostics));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _folderShowSelection = folderShowSelection ?? throw new ArgumentNullException(nameof(folderShowSelection));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _theme = theme ?? throw new ArgumentNullException(nameof(theme));
        _transcriptionAdministration = transcriptionAdministration ?? throw new ArgumentNullException(nameof(transcriptionAdministration));
        _dataDirectory = dataDirectory;
        _diagnosticLogPath = diagnosticLogPath;
        IsRemoteSession = isRemoteSession;
        _versionText = versionText;
        _statusText = isRemoteSession
            ? "Local settings."
            : "Archive, playback, appearance and maintenance settings.";
        _anywhereSnapshot = _anywhere.Current;
        _anywhere.StateChanged += AnywhereOnStateChanged;

        Sections = new ObservableCollection<SettingsSectionItemViewModel>(new[]
        {
            new SettingsSectionItemViewModel("archive", "Archive", "Folders, health and backup", SelectSection),
            new SettingsSectionItemViewModel("playback", "Playback", "Skipping and completion", SelectSection),
            new SettingsSectionItemViewModel("appearance", "Appearance", "Theme and presentation", SelectSection),
            new SettingsSectionItemViewModel("connected", "Server connection", "See or change the server this client uses", SelectSection),
            new SettingsSectionItemViewModel("anywhere", "Radio Vault Web", "Phone and browser access hosted by your server", SelectSection),
            new SettingsSectionItemViewModel("transcription", "Transcription", "Models and processing on the active server", SelectSection),
            new SettingsSectionItemViewModel("advanced", "Advanced", "Diagnostics and technical details", SelectSection)
        });
        SelectSection(Sections[0]);

        RefreshCommand = new AsyncCommand(() => LoadAsync(force: true), () => !IsBusy, SetError);
        AddFolderCommand = new AsyncCommand(AddFolderAsync, () => !IsBusy && CanManageLibrary, SetError);
        ToggleFolderCommand = new AsyncCommand(ToggleFolderAsync, () => !IsBusy && CanManageLibrary && SelectedFolder is not null, SetError);
        RemoveFolderCommand = new AsyncCommand(RemoveFolderAsync, () => !IsBusy && CanManageLibrary && SelectedFolder is not null, SetError);
        AnalyseHealthCommand = new AsyncCommand(AnalyseHealthAsync, () => !IsBusy && CanAnalyseHealth, SetError);
        ScanLibraryCommand = new AsyncCommand(ScanLibraryAsync, () => !IsBusy && CanScanLibrary && !IsScanRunning, SetError);
        CreateBackupCommand = new AsyncCommand(CreateBackupAsync, () => !IsBusy && _backup is not null && !IsRemoteSession, SetError);
        RestoreBackupCommand = new AsyncCommand(RestoreBackupAsync, () => !IsBusy && _backup is not null && !IsRemoteSession, SetError);
        SavePlaybackCommand = new AsyncCommand(SavePlaybackAsync, () => !IsBusy, SetError);
        SaveTranscriptionCommand = new AsyncCommand(SaveTranscriptionAsync, () => !IsBusy, SetError);
        BrowseTranscriptionExecutableCommand = new AsyncCommand(() => Task.CompletedTask, () => false, SetError);
        BrowseTranscriptionModelCommand = new AsyncCommand(() => Task.CompletedTask, () => false, SetError);
        BrowseTranscriptionVadCommand = new AsyncCommand(() => Task.CompletedTask, () => false, SetError);
        OpenTranscriptionModelsCommand = new AsyncCommand(() => Task.CompletedTask, () => false, SetError);
        InstallRecommendedTranscriptionCommand = new AsyncCommand(InstallRecommendedTranscriptionAsync, () => !IsBusy, SetError);
        DownloadTranscriptionWorkerCommand = new AsyncCommand(() => Task.CompletedTask, () => false, SetError);
        DownloadTranscriptionModelCommand = new AsyncCommand(() => Task.CompletedTask, () => false, SetError);
        DownloadTranscriptionVadCommand = new AsyncCommand(() => Task.CompletedTask, () => false, SetError);
        DownloadDiarizationModelsCommand = new AsyncCommand(() => Task.CompletedTask, () => false, SetError);
        CancelTranscriptionDownloadCommand = new DelegateCommand(CancelTranscriptionDownload, () => IsTranscriptionDownloadActive);
        SaveAnywhereCommand = new AsyncCommand(SaveAnywhereAsync, () => !IsBusy && AnywhereCanManage, SetError);
        StartAnywhereCommand = new AsyncCommand(StartAnywhereAsync, () => !IsBusy && AnywhereCanManage && !AnywhereSnapshot.IsRunning, SetError);
        StopAnywhereCommand = new AsyncCommand(StopAnywhereAsync, () => !IsBusy && AnywhereCanManage && AnywhereSnapshot.IsRunning, SetError);
        GeneratePairingCodeCommand = new AsyncCommand(GeneratePairingCodeAsync, () => !IsBusy && AnywhereCanManage, SetError);
        RevokePairedClientCommand = new AsyncCommand(RevokePairedClientAsync, () => !IsBusy && AnywhereCanManage && SelectedPairedClient is not null, SetError);
        CopyAnywhereLinkCommand = new AsyncCommand(CopyAnywhereLinkAsync, () => !IsBusy && AnywhereSnapshot.HasAccessUrl, SetError);
        OpenAnywhereLinkCommand = new AsyncCommand(OpenAnywhereLinkAsync, () => !IsBusy && AnywhereSnapshot.HasAccessUrl, SetError);
        OpenAnywhereSetupCommand = new AsyncCommand(OpenAnywhereSetupAsync, () => !IsBusy && AnywhereSnapshot.HasSetupUrl, SetError);
        RegenerateAnywhereLinkCommand = new AsyncCommand(RegenerateAnywhereLinkAsync, () => !IsBusy && AnywhereCanManage, SetError);
        ResetAnywhereCertificatesCommand = new AsyncCommand(ResetAnywhereCertificatesAsync, () => !IsBusy && AnywhereCanManage, SetError);
        DiagnoseAnywhereCommand = new AsyncCommand(DiagnoseAnywhereAsync, () => !IsBusy && AnywhereSnapshot.IsAvailable, SetError);
        RunQuickDiagnosticsCommand = new AsyncCommand(() => RunConnectedDiagnosticsAsync(ConnectedPlaybackDiagnosticMode.Quick), () => !IsBusy && !IsDiagnosticsRunning, SetError);
        RunStressDiagnosticsCommand = new AsyncCommand(() => RunConnectedDiagnosticsAsync(ConnectedPlaybackDiagnosticMode.Stress), () => !IsBusy && !IsDiagnosticsRunning, SetError);
        CancelDiagnosticsCommand = new DelegateCommand(CancelConnectedDiagnostics, () => IsDiagnosticsRunning);
        ExportDiagnosticsCommand = new AsyncCommand(ExportConnectedDiagnosticsAsync, () => !IsDiagnosticsRunning && HasDiagnosticReport, SetError);
        CopyDiagnosticsSummaryCommand = new AsyncCommand(CopyConnectedDiagnosticsSummaryAsync, () => HasDiagnosticReport || DiagnosticSteps.Count > 0, SetError);
        OpenDataFolderCommand = new AsyncCommand(() => _launcher.LaunchAsync(ExternalLaunchRequest.Folder(_dataDirectory)), () => !IsBusy, SetError);
        OpenDiagnosticLogCommand = new AsyncCommand(OpenDiagnosticLogAsync, () => !IsBusy, SetError);
    }

    public ObservableCollection<SettingsSectionItemViewModel> Sections { get; }
    public ConnectedAccessViewModel ConnectedAccess { get; }
    public ObservableCollection<LibraryFolderRecord> LibraryFolders { get; } = new();
    public ObservableCollection<ConnectedPlaybackDiagnosticStep> DiagnosticSteps { get; } = new();
    public IReadOnlyList<AppThemeMode> ThemeModes { get; } = Enum.GetValues<AppThemeMode>();
    public IReadOnlyList<int> PlaybackIntervalOptions => PlaybackIntervals;
    public IReadOnlyList<int> CompletionThresholdOptions => CompletionThresholds;
    public IReadOnlyList<string> TranscriptionLanguageOptions { get; } = new[] { "auto", "en" };
    public IReadOnlyList<WhisperModelCatalogItem> TranscriptionModelOptions => WhisperModelCatalog.Items;

    public AppThemeMode SelectedThemeMode
    {
        get => _theme.CurrentMode;
        set
        {
            if (_theme.CurrentMode == value) return;
            _theme.Apply(value);
            RaisePropertyChanged();
            StatusText = value == AppThemeMode.System ? "Following the system appearance setting." : $"{value} appearance selected.";
        }
    }

    public ICommand RefreshCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand ToggleFolderCommand { get; }
    public ICommand RemoveFolderCommand { get; }
    public ICommand AnalyseHealthCommand { get; }
    public ICommand ScanLibraryCommand { get; }
    public ICommand CreateBackupCommand { get; }
    public ICommand RestoreBackupCommand { get; }
    public ICommand SavePlaybackCommand { get; }
    public ICommand SaveTranscriptionCommand { get; }
    public ICommand BrowseTranscriptionExecutableCommand { get; }
    public ICommand BrowseTranscriptionModelCommand { get; }
    public ICommand BrowseTranscriptionVadCommand { get; }
    public ICommand OpenTranscriptionModelsCommand { get; }
    public ICommand InstallRecommendedTranscriptionCommand { get; }
    public ICommand DownloadTranscriptionWorkerCommand { get; }
    public ICommand DownloadTranscriptionModelCommand { get; }
    public ICommand DownloadTranscriptionVadCommand { get; }
    public ICommand DownloadDiarizationModelsCommand { get; }
    public ICommand CancelTranscriptionDownloadCommand { get; }
    public ICommand SaveAnywhereCommand { get; }
    public ICommand StartAnywhereCommand { get; }
    public ICommand StopAnywhereCommand { get; }
    public ICommand GeneratePairingCodeCommand { get; }
    public ICommand RevokePairedClientCommand { get; }
    public ICommand CopyAnywhereLinkCommand { get; }
    public ICommand OpenAnywhereLinkCommand { get; }
    public ICommand OpenAnywhereSetupCommand { get; }
    public ICommand RegenerateAnywhereLinkCommand { get; }
    public ICommand ResetAnywhereCertificatesCommand { get; }
    public ICommand DiagnoseAnywhereCommand { get; }
    public ICommand RunQuickDiagnosticsCommand { get; }
    public ICommand RunStressDiagnosticsCommand { get; }
    public ICommand CancelDiagnosticsCommand { get; }
    public ICommand ExportDiagnosticsCommand { get; }
    public ICommand CopyDiagnosticsSummaryCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand OpenDiagnosticLogCommand { get; }

    public bool IsRemoteSession { get; }
    public bool CanManageLibrary => false;
    public bool CanAnalyseHealth => _health is not null;
    public bool CanScanLibrary => _libraryMaintenance.IsAvailable;
    public LibraryMaintenanceSnapshot LibraryScan { get => _libraryScan; private set { if (SetProperty(ref _libraryScan, value)) RaiseLibraryScanProperties(); } }
    public bool IsScanRunning => LibraryScan.IsRunning;
    public string LibraryScanStatusText => LibraryScan.StatusText;
    public string LibraryScanResultText => LibraryScan.ResultText;
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandState(); } }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public SettingsSectionItemViewModel? SelectedSection { get => _selectedSection; private set => SetProperty(ref _selectedSection, value); }
    public bool IsArchiveSection => SelectedSection?.Key == "archive";
    public bool IsPlaybackSection => SelectedSection?.Key == "playback";
    public bool IsAppearanceSection => SelectedSection?.Key == "appearance";
    public bool IsConnectedSection => SelectedSection?.Key == "connected";
    public bool IsAnywhereSection => SelectedSection?.Key == "anywhere";
    public bool IsTranscriptionSection => SelectedSection?.Key == "transcription";
    public bool IsAdvancedSection => SelectedSection?.Key == "advanced";
    public string SelectedSectionTitle => SelectedSection?.Title ?? "Settings";
    public string SelectedSectionDescription => SelectedSection?.Description ?? string.Empty;

    public ArchiveHealthReport? HealthReport { get => _healthReport; private set { if (SetProperty(ref _healthReport, value)) RaiseHealthProperties(); } }
    public bool HasHealthReport => HealthReport is not null;
    public string HealthScoreText => HealthReport is null ? "—" : $"{HealthReport.HealthScore}%";
    public string HealthLabel => HealthReport?.HealthLabel ?? "Not analysed";
    public string HealthBreakdown => HealthReport?.ScoreBreakdown ?? "Run Archive Health to inspect collection, metadata, research and preservation.";
    public string HealthIssuesText => HealthReport is null ? "No report yet" : $"{HealthReport.ActionableIssues:N0} actionable issues";
    public LibraryFolderRecord? SelectedFolder { get => _selectedFolder; set { if (SetProperty(ref _selectedFolder, value)) RaiseCommandState(); } }
    public bool HasFolders => LibraryFolders.Count > 0;

    public int SkipBackSeconds { get => _skipBackSeconds; set => SetProperty(ref _skipBackSeconds, value); }
    public int SkipForwardSeconds { get => _skipForwardSeconds; set => SetProperty(ref _skipForwardSeconds, value); }
    public int CompletionThresholdSeconds { get => _completionThresholdSeconds; set => SetProperty(ref _completionThresholdSeconds, value); }
    public bool ConfirmResetToUnplayed { get => _confirmResetToUnplayed; set => SetProperty(ref _confirmResetToUnplayed, value); }

    public string TranscriptionExecutablePath { get => _transcriptionExecutablePath; set => SetProperty(ref _transcriptionExecutablePath, value); }
    public string TranscriptionModelPath { get => _transcriptionModelPath; set => SetProperty(ref _transcriptionModelPath, value); }
    public string TranscriptionVadModelPath { get => _transcriptionVadModelPath; set => SetProperty(ref _transcriptionVadModelPath, value); }
    public string DiarizationSegmentationModelPath { get => _diarizationSegmentationModelPath; set { if (SetProperty(ref _diarizationSegmentationModelPath, value)) RaisePropertyChanged(nameof(TranscriptionStatusText)); } }
    public string DiarizationEmbeddingModelPath { get => _diarizationEmbeddingModelPath; set { if (SetProperty(ref _diarizationEmbeddingModelPath, value)) RaisePropertyChanged(nameof(TranscriptionStatusText)); } }
    public string TranscriptionLanguage { get => _transcriptionLanguage; set => SetProperty(ref _transcriptionLanguage, value); }
    public int TranscriptionThreads { get => _transcriptionThreads; set => SetProperty(ref _transcriptionThreads, Math.Clamp(value, 0, 128)); }
    public bool TranscriptionUseGpu { get => _transcriptionUseGpu; set => SetProperty(ref _transcriptionUseGpu, value); }
    public bool TranscriptionUseVad { get => _transcriptionUseVad; set => SetProperty(ref _transcriptionUseVad, value); }
    public bool TranscriptionMultiSpeakerDiarization { get => _transcriptionMultiSpeakerDiarization; set { if (SetProperty(ref _transcriptionMultiSpeakerDiarization, value)) RaisePropertyChanged(nameof(TranscriptionStatusText)); } }
    public bool TranscriptionUseArchiveContext { get => _transcriptionUseArchiveContext; set => SetProperty(ref _transcriptionUseArchiveContext, value); }
    public WhisperModelCatalogItem SelectedTranscriptionModel { get => _selectedTranscriptionModel; set => SetProperty(ref _selectedTranscriptionModel, value); }
    public bool IsTranscriptionDownloadActive
    {
        get => _isTranscriptionDownloadActive;
        private set
        {
            if (!SetProperty(ref _isTranscriptionDownloadActive, value)) return;
            RaisePropertyChanged(nameof(IsNotTranscriptionDownloadActive));
            RaiseCommandState();
        }
    }
    public bool IsNotTranscriptionDownloadActive => !IsTranscriptionDownloadActive;
    public double? TranscriptionDownloadPercent
    {
        get => _transcriptionDownloadPercent;
        private set
        {
            if (!SetProperty(ref _transcriptionDownloadPercent, value)) return;
            RaisePropertyChanged(nameof(HasTranscriptionDownloadPercent));
        }
    }
    public bool HasTranscriptionDownloadPercent => TranscriptionDownloadPercent.HasValue;
    public string TranscriptionStatusText => _serverTranscriptionStatus.IsAvailable
        ? _serverTranscriptionStatus.DiarizationAvailable
            ? "The active server is ready for transcription and multi-speaker diarization."
            : "The active server is ready for transcription. Multi-speaker diarization still needs attention."
        : _serverTranscriptionStatus.AvailabilityMessage;

    public RadioVaultAnywhereSnapshot AnywhereSnapshot
    {
        get => _anywhereSnapshot;
        private set
        {
            if (!SetProperty(ref _anywhereSnapshot, value)) return;
            ApplyAnywhereSnapshot(value);
            RaiseAnywhereProperties();
        }
    }
    public bool AnywhereCanManage => AnywhereSnapshot.CanManage;
    public bool AnywhereEnabled { get => _anywhereEnabled; set => SetProperty(ref _anywhereEnabled, value); }
    public bool AnywhereStartAutomatically { get => _anywhereStartAutomatically; set => SetProperty(ref _anywhereStartAutomatically, value); }
    public string AnywhereServerName { get => _anywhereServerName; set => SetProperty(ref _anywhereServerName, value); }
    public int AnywhereHttpPort { get => _anywhereHttpPort; set => SetProperty(ref _anywhereHttpPort, value); }
    public bool AnywhereSecureAccess { get => _anywhereSecureAccess; set => SetProperty(ref _anywhereSecureAccess, value); }
    public int AnywhereHttpsPort { get => _anywhereHttpsPort; set => SetProperty(ref _anywhereHttpsPort, value); }
    public int AnywhereDiscoveryPort { get => _anywhereDiscoveryPort; set => SetProperty(ref _anywhereDiscoveryPort, value); }
    public RadioVaultAnywhereClient? SelectedPairedClient { get => _selectedPairedClient; set { if (SetProperty(ref _selectedPairedClient, value)) RaiseCommandState(); } }
    public string AnywhereAccessUrl => AnywhereSnapshot.AccessUrl;
    public PhoneQrCode AnywhereQrCode { get => _anywhereQrCode; private set => SetProperty(ref _anywhereQrCode, value); }
    public PhoneQrCode AnywhereSetupQrCode { get => _anywhereSetupQrCode; private set => SetProperty(ref _anywhereSetupQrCode, value); }
    public string AnywhereSetupUrl => AnywhereSnapshot.SetupUrl;
    public string AnywherePairingCode => AnywhereSnapshot.PairingCode;
    public string AnywherePairingExpiryText => AnywhereSnapshot.PairingExpiryText;
    public bool HasAnywherePairingCode => AnywhereSnapshot.HasPairingCode;
    public bool HasAnywhereClients => AnywhereSnapshot.HasPairedClients;
    public IReadOnlyList<RadioVaultAnywhereClient> AnywhereClients => AnywhereSnapshot.PairedClients;
    public IReadOnlyList<string> AnywhereDiagnostics => AnywhereSnapshot.DiagnosticChecks;
    public bool HasAnywhereDiagnostics => AnywhereSnapshot.DiagnosticChecks.Count > 0;

    public string VersionText => _versionText;
    public string DatabaseSchemaText => "45";
    public string DataDirectoryText => _dataDirectory;
    public string DiagnosticLogText => _diagnosticLogPath;
    public bool IsDiagnosticsRunning
    {
        get => _isDiagnosticsRunning;
        private set
        {
            if (!SetProperty(ref _isDiagnosticsRunning, value)) return;
            RaisePropertyChanged(nameof(DiagnosticsRunStateText));
            RaiseDiagnosticCommandState();
        }
    }
    public string DiagnosticSessionCode
    {
        get => _diagnosticSessionCode;
        set => SetProperty(ref _diagnosticSessionCode, value ?? string.Empty);
    }
    public string DiagnosticSummary
    {
        get => _diagnosticSummary;
        private set => SetProperty(ref _diagnosticSummary, value);
    }
    public string DiagnosticsRunStateText => IsDiagnosticsRunning ? "Diagnostic test running…" : "Ready";
    public bool HasDiagnosticReport => _diagnosticReport is not null;
    public bool HasDiagnosticSteps => DiagnosticSteps.Count > 0;

    public async Task LoadAsync(bool force = false)
    {
        if (_isLoaded && !force) return;
        IsBusy = true;
        try
        {
            await ConnectedAccess.InitializeAsync().ConfigureAwait(true);
            if (ConnectedAccess.Snapshot.State is ConnectedAccessState.Disconnected or ConnectedAccessState.Unavailable)
                SelectSectionByKey("connected");

            LibraryFolders.Clear();
            if (_folders is not null)
            {
                try
                {
                    foreach (var folder in await _folders.GetAllAsync().ConfigureAwait(true))
                        LibraryFolders.Add(folder);
                }
                catch (Exception exception)
                {
                    StatusText = $"Server Library folders could not be loaded: {exception.Message}";
                }
            }
            RaisePropertyChanged(nameof(HasFolders));
            SelectedFolder = LibraryFolders.FirstOrDefault();
            LoadPlaybackPreferences();
            try
            {
                await LoadTranscriptionPreferencesAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                _serverTranscriptionStatus = _serverTranscriptionStatus with
                {
                    AvailabilityMessage = exception.Message
                };
                RaisePropertyChanged(nameof(TranscriptionStatusText));
            }
            try
            {
                LibraryScan = await _libraryMaintenance.GetStatusAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                LibraryScan = LibraryScan with { Message = exception.Message };
            }
            RaisePropertyChanged(nameof(CanScanLibrary));
            if (_health is not null)
            {
                try
                {
                    HealthReport = await _health.AnalyseAsync().ConfigureAwait(true);
                }
                catch (Exception exception)
                {
                    StatusText = $"Server Archive Health could not be loaded: {exception.Message}";
                }
            }
            try
            {
                AnywhereSnapshot = await _anywhere.RefreshAsync().ConfigureAwait(true);
            }
            catch (Exception exception)
            {
                AnywhereSnapshot = AnywhereSnapshot with
                {
                    StatusText = "Radio Vault Web is unavailable until a server is connected.",
                    DetailText = exception.Message
                };
            }

            StatusText = ConnectedAccess.Snapshot.State switch
            {
                ConnectedAccessState.Disconnected => "Find and pair with your Radio Vault Server to open the Library and playback views.",
                ConnectedAccessState.Unavailable => "Start Radio Vault Server or pair this client with a server on your network.",
                _ when AnywhereSnapshot.IsRunning => AnywhereSnapshot.StatusText,
                _ => $"{LibraryFolders.Count:N0} server Library folder{(LibraryFolders.Count == 1 ? string.Empty : "s")} registered."
            };
            _isLoaded = true;
        }
        finally { IsBusy = false; }
    }

    private void SelectSection(SettingsSectionItemViewModel item)
    {
        foreach (var section in Sections) section.IsSelected = ReferenceEquals(section, item);
        SelectedSection = item;
        RaisePropertyChanged(nameof(IsArchiveSection));
        RaisePropertyChanged(nameof(IsPlaybackSection));
        RaisePropertyChanged(nameof(IsAppearanceSection));
        RaisePropertyChanged(nameof(IsConnectedSection));
        RaisePropertyChanged(nameof(IsAnywhereSection));
        RaisePropertyChanged(nameof(IsTranscriptionSection));
        RaisePropertyChanged(nameof(IsAdvancedSection));
        RaisePropertyChanged(nameof(SelectedSectionTitle));
        RaisePropertyChanged(nameof(SelectedSectionDescription));
    }

    private async Task AddFolderAsync()
    {
        if (_folders is null) return;
        var path = await _files.PickFolderAsync(new FileSelectionRequest(Title: "Choose a Radio Vault archive folder")).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;

        var showChoices = new List<LibraryFolderShowChoice>
        {
            new(
                CollectionId: null,
                Name: "Auto-detect / mixed-show folder",
                Description: "Classify each recording from its folder path and filename.")
        };
        showChoices.AddRange((await _folders.GetAssignableCollectionsAsync().ConfigureAwait(true))
            .Select(collection => new LibraryFolderShowChoice(
                collection.CollectionId,
                collection.Name,
                $"Treat recordings in this folder as {collection.Name} unless a more specific filename rule applies.")));

        var selection = await _folderShowSelection
            .ChooseAsync(path, showChoices)
            .ConfigureAwait(true);
        if (selection is null) return;

        await _folders.AddAsync(path, selection.CollectionId, recursive: true).ConfigureAwait(true);
        _isLoaded = false;
        await LoadAsync(force: true).ConfigureAwait(true);
        StatusText = selection.CollectionId.HasValue
            ? $"Archive folder added for {selection.Name}."
            : "Archive folder added with automatic show detection.";
    }

    private async Task ToggleFolderAsync()
    {
        if (_folders is null || SelectedFolder is null) return;
        await _folders.SetEnabledAsync(SelectedFolder.Id, !SelectedFolder.Enabled).ConfigureAwait(true);
        _isLoaded = false;
        await LoadAsync(force: true).ConfigureAwait(true);
    }

    private async Task RemoveFolderAsync()
    {
        if (_folders is null || SelectedFolder is null) return;
        await _folders.RemoveAsync(SelectedFolder.Id).ConfigureAwait(true);
        _isLoaded = false;
        await LoadAsync(force: true).ConfigureAwait(true);
        StatusText = "Library folder registration removed. Audio files were not deleted.";
    }

    private async Task AnalyseHealthAsync()
    {
        if (_health is null) return;
        IsBusy = true;
        StatusText = "Analysing Archive Health…";
        try
        {
            HealthReport = await _health.AnalyseAsync().ConfigureAwait(true);
            StatusText = $"Archive Health: {HealthReport.HealthScore}% · {HealthReport.ActionableIssues:N0} actionable issues.";
        }
        finally { IsBusy = false; }
    }

    private async Task ScanLibraryAsync()
    {
        IsBusy = true;
        StatusText = "Scanning registered archive folders…";
        try
        {
            var scanTask = _libraryMaintenance.ScanAsync();
            while (!scanTask.IsCompleted)
            {
                await Task.Delay(250).ConfigureAwait(true);
                LibraryScan = await _libraryMaintenance.GetStatusAsync().ConfigureAwait(true);
                if (!string.IsNullOrWhiteSpace(LibraryScan.Message))
                    StatusText = LibraryScan.Message;
            }

            LibraryScan = await scanTask.ConfigureAwait(true);
            if (_health is not null)
                HealthReport = await _health.AnalyseAsync().ConfigureAwait(true);
            if (_folders is not null)
            {
                LibraryFolders.Clear();
                foreach (var folder in await _folders.GetAllAsync().ConfigureAwait(true))
                    LibraryFolders.Add(folder);
                RaisePropertyChanged(nameof(HasFolders));
            }
            StatusText = LibraryScan.Message;
            LibraryScanCompleted?.Invoke(this, EventArgs.Empty);
        }
        finally { IsBusy = false; }
    }

    private async Task CreateBackupAsync()
    {
        if (_backup is null) return;
        var path = await _files.PickSaveFileAsync(new FileSelectionRequest(
            Title: "Create a Radio Vault backup",
            Filter: "Radio Vault backup|*.trvbackup",
            DefaultExtension: ".trvbackup",
            SuggestedFileName: $"RadioVault-{DateTime.Now:yyyy-MM-dd-HHmm}.trvbackup",
            CheckFileExists: false)).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;
        IsBusy = true;
        StatusText = "Creating a safe database and artwork backup…";
        try
        {
            var result = await _backup.CreateAsync(path).ConfigureAwait(true);
            StatusText = $"Backup created: {Path.GetFileName(result)}";
        }
        finally { IsBusy = false; }
    }

    private async Task RestoreBackupAsync()
    {
        if (_backup is null) return;
        var path = await _files.PickOpenFileAsync(new FileSelectionRequest(
            Title: "Restore a Radio Vault backup",
            Filter: "Radio Vault backup|*.trvbackup")).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;
        IsBusy = true;
        StatusText = "Restoring the backup and preserving this computer's Library roots…";
        try
        {
            var result = await _backup.RestoreAsync(path).ConfigureAwait(true);
            StatusText = result.Message;
        }
        finally { IsBusy = false; }
    }

    private Task SavePlaybackAsync()
    {
        var path = Path.Combine(_dataDirectory, "playback.json");
        var document = ReadObject(path);
        document["SkipBackSeconds"] = Normalise(SkipBackSeconds, PlaybackIntervals, 30);
        document["SkipForwardSeconds"] = Normalise(SkipForwardSeconds, PlaybackIntervals, 30);
        document["CompletionThresholdSeconds"] = Normalise(CompletionThresholdSeconds, CompletionThresholds, 5);
        document["ConfirmResetToUnplayed"] = ConfirmResetToUnplayed;
        WriteObject(path, document);
        _playback.ApplyPlaybackPreferences(SkipBackSeconds, SkipForwardSeconds, CompletionThresholdSeconds);
        StatusText = "Playback settings saved and applied.";
        return Task.CompletedTask;
    }

    private void LoadPlaybackPreferences()
    {
        var document = ReadObject(Path.Combine(_dataDirectory, "playback.json"));
        SkipBackSeconds = Normalise(document["SkipBackSeconds"]?.GetValue<int>() ?? 30, PlaybackIntervals, 30);
        SkipForwardSeconds = Normalise(document["SkipForwardSeconds"]?.GetValue<int>() ?? 30, PlaybackIntervals, 30);
        CompletionThresholdSeconds = Normalise(document["CompletionThresholdSeconds"]?.GetValue<int>() ?? 5, CompletionThresholds, 5);
        ConfirmResetToUnplayed = document["ConfirmResetToUnplayed"]?.GetValue<bool>() ?? true;
        _playback.ApplyPlaybackPreferences(SkipBackSeconds, SkipForwardSeconds, CompletionThresholdSeconds);
    }

    private async Task SaveTranscriptionAsync()
    {
        var settings = CreateTranscriptionSettings();
        var snapshot = await _transcriptionAdministration.SaveAsync(settings).ConfigureAwait(true);
        ApplyTranscriptionSnapshot(snapshot);
        RaisePropertyChanged(nameof(TranscriptionStatusText));
        StatusText = $"Transcription configuration saved on {ConnectedAccess.ActiveServerName}. {snapshot.Status.AvailabilityMessage}";
    }

    private async Task LoadTranscriptionPreferencesAsync()
    {
        var snapshot = await _transcriptionAdministration.GetAsync().ConfigureAwait(true);
        ApplyTranscriptionSnapshot(snapshot);
    }

    private void ApplyTranscriptionSnapshot(ServerTranscriptionAdministrationSnapshot snapshot)
    {
        _serverTranscriptionStatus = snapshot.Status;
        var settings = snapshot.Settings;
        TranscriptionExecutablePath = settings.ExecutablePath;
        TranscriptionModelPath = settings.ModelPath;
        TranscriptionVadModelPath = settings.VadModelPath;
        DiarizationSegmentationModelPath = settings.DiarizationSegmentationModelPath;
        DiarizationEmbeddingModelPath = settings.DiarizationEmbeddingModelPath;
        TranscriptionLanguage = settings.DefaultLanguage;
        TranscriptionThreads = settings.Threads;
        TranscriptionUseGpu = settings.UseGpu;
        TranscriptionUseVad = settings.UseVoiceActivityDetection;
        TranscriptionMultiSpeakerDiarization = settings.EnableMultiSpeakerDiarization;
        TranscriptionUseArchiveContext = settings.UseArchiveContext;
        SelectedTranscriptionModel = WhisperModelCatalog.Items.FirstOrDefault(x => string.Equals(x.FileName, Path.GetFileName(settings.ModelPath), StringComparison.OrdinalIgnoreCase))
            ?? WhisperModelCatalog.Items.First(x => x.Id == "base.en");
        RaisePropertyChanged(nameof(TranscriptionStatusText));
    }

    private WhisperCppEngineSettings CreateTranscriptionSettings() => new()
    {
        ExecutablePath = TranscriptionExecutablePath,
        ModelPath = TranscriptionModelPath,
        VadModelPath = TranscriptionVadModelPath,
        DiarizationSegmentationModelPath = DiarizationSegmentationModelPath,
        DiarizationEmbeddingModelPath = DiarizationEmbeddingModelPath,
        DefaultLanguage = TranscriptionLanguage,
        Threads = TranscriptionThreads,
        UseGpu = TranscriptionUseGpu,
        UseVoiceActivityDetection = TranscriptionUseVad,
        EnableMultiSpeakerDiarization = TranscriptionMultiSpeakerDiarization,
        UseArchiveContext = TranscriptionUseArchiveContext
    };

    public void SelectSectionByKey(string key)
    {
        var section = Sections.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
        if (section is not null) SelectSection(section);
    }

    private async Task InstallRecommendedTranscriptionAsync()
    {
        _transcriptionDownloadCancellation?.Dispose();
        _transcriptionDownloadCancellation = new CancellationTokenSource();
        IsBusy = true;
        IsTranscriptionDownloadActive = true;
        TranscriptionDownloadPercent = null;
        StatusText = $"Asking {ConnectedAccess.ActiveServerName} to install the recommended transcription setup…";
        try
        {
            var snapshot = await _transcriptionAdministration.InstallRecommendedAsync(
                SelectedTranscriptionModel.Id,
                _transcriptionDownloadCancellation.Token).ConfigureAwait(true);
            ApplyTranscriptionSnapshot(snapshot);
            StatusText = $"{ConnectedAccess.ActiveServerName} is ready with {SelectedTranscriptionModel.DisplayName}, VAD and multi-speaker diarization.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "The server transcription setup request was cancelled. Existing files and settings were left unchanged.";
        }
        finally
        {
            IsTranscriptionDownloadActive = false;
            IsBusy = false;
            _transcriptionDownloadCancellation.Dispose();
            _transcriptionDownloadCancellation = null;
        }
    }

    private void CancelTranscriptionDownload()
    {
        _transcriptionDownloadCancellation?.Cancel();
        StatusText = "Cancelling the server transcription setup request…";
    }

    private async Task SaveAnywhereAsync()
    {
        IsBusy = true;
        try
        {
            await _anywhere.SaveAsync(new RadioVaultAnywhereSettings(
                AnywhereEnabled,
                AnywhereStartAutomatically,
                AnywhereServerName,
                AnywhereHttpPort,
                AnywhereSecureAccess,
                AnywhereHttpsPort,
                AnywhereDiscoveryPort)).ConfigureAwait(true);
            AnywhereSnapshot = _anywhere.Current;
            StatusText = AnywhereSnapshot.StatusText;
        }
        finally { IsBusy = false; }
    }

    private async Task StartAnywhereAsync()
    {
        IsBusy = true;
        try
        {
            await _anywhere.SaveAsync(new RadioVaultAnywhereSettings(
                true, AnywhereStartAutomatically, AnywhereServerName, AnywhereHttpPort,
                AnywhereSecureAccess, AnywhereHttpsPort, AnywhereDiscoveryPort)).ConfigureAwait(true);
            await _anywhere.StartAsync().ConfigureAwait(true);
            AnywhereSnapshot = _anywhere.Current;
            StatusText = AnywhereSnapshot.StatusText;
        }
        finally { IsBusy = false; }
    }

    private async Task StopAnywhereAsync()
    {
        IsBusy = true;
        try
        {
            await _anywhere.StopAsync().ConfigureAwait(true);
            AnywhereSnapshot = _anywhere.Current;
            StatusText = AnywhereSnapshot.StatusText;
        }
        finally { IsBusy = false; }
    }

    private async Task GeneratePairingCodeAsync()
    {
        await _anywhere.GeneratePairingCodeAsync().ConfigureAwait(true);
        AnywhereSnapshot = _anywhere.Current;
        StatusText = "Browser pairing code created.";
    }

    private async Task RevokePairedClientAsync()
    {
        if (SelectedPairedClient is null) return;
        await _anywhere.RevokeClientAsync(SelectedPairedClient.ClientId).ConfigureAwait(true);
        SelectedPairedClient = null;
        AnywhereSnapshot = _anywhere.Current;
        StatusText = "Browser access revoked.";
    }

    private async Task CopyAnywhereLinkAsync()
    {
        if (!AnywhereSnapshot.HasAccessUrl) return;
        await _clipboard.SetTextAsync(AnywhereSnapshot.AccessUrl).ConfigureAwait(true);
        StatusText = "Private Radio Vault Web link copied.";
    }

    private Task OpenAnywhereLinkAsync()
        => AnywhereSnapshot.HasAccessUrl
            ? _launcher.LaunchAsync(ExternalLaunchRequest.Uri(new Uri(AnywhereSnapshot.AccessUrl)))
            : Task.CompletedTask;

    private Task OpenAnywhereSetupAsync()
        => AnywhereSnapshot.HasSetupUrl
            ? _launcher.LaunchAsync(ExternalLaunchRequest.Uri(new Uri(AnywhereSnapshot.SetupUrl)))
            : Task.CompletedTask;

    private async Task RegenerateAnywhereLinkAsync()
    {
        await _anywhere.RegeneratePrivateLinkAsync().ConfigureAwait(true);
        AnywhereSnapshot = _anywhere.Current;
        StatusText = "A new private link was generated. Old saved links will no longer work.";
    }

    private async Task ResetAnywhereCertificatesAsync()
    {
        await _anywhere.ResetCertificatesAsync().ConfigureAwait(true);
        AnywhereSnapshot = _anywhere.Current;
        StatusText = "Secure certificates reset. Devices must trust the new certificate.";
    }

    private async Task RunConnectedDiagnosticsAsync(ConnectedPlaybackDiagnosticMode mode)
    {
        _diagnosticCancellation?.Cancel();
        _diagnosticCancellation?.Dispose();
        _diagnosticCancellation = new CancellationTokenSource();
        DiagnosticSteps.Clear();
        _diagnosticReport = null;
        RaisePropertyChanged(nameof(HasDiagnosticReport));
        RaisePropertyChanged(nameof(HasDiagnosticSteps));
        IsDiagnosticsRunning = true;
        DiagnosticSummary = mode == ConnectedPlaybackDiagnosticMode.Quick
            ? "Running the quick connected-playback test…"
            : "Running the full connected-playback stress test…";
        StatusText = DiagnosticSummary;
        var progress = new Progress<ConnectedPlaybackDiagnosticProgress>(update =>
        {
            var existing = DiagnosticSteps.FirstOrDefault(x => x.Key == update.Step.Key);
            if (existing is not null)
            {
                var index = DiagnosticSteps.IndexOf(existing);
                if (index >= 0) DiagnosticSteps[index] = update.Step;
            }
            else DiagnosticSteps.Add(update.Step);
            RaisePropertyChanged(nameof(HasDiagnosticSteps));
            DiagnosticSummary = update.Summary;
        });

        try
        {
            _diagnosticReport = await _connectedDiagnostics.RunAsync(
                mode,
                DiagnosticSessionCode,
                progress,
                _diagnosticCancellation.Token).ConfigureAwait(true);
            DiagnosticSessionCode = _diagnosticReport.SessionCode;
            DiagnosticSummary = _diagnosticReport.Summary;
            StatusText = _diagnosticReport.OverallStatus == ConnectedPlaybackDiagnosticStatus.Failed
                ? "Connected playback diagnostics found one or more failures. Export the report for investigation."
                : _diagnosticReport.Summary;
            RaisePropertyChanged(nameof(HasDiagnosticReport));
        }
        catch (OperationCanceledException)
        {
            DiagnosticSummary = "The diagnostic run was cancelled.";
            StatusText = DiagnosticSummary;
        }
        finally
        {
            IsDiagnosticsRunning = false;
        }
    }

    private void CancelConnectedDiagnostics()
    {
        if (!IsDiagnosticsRunning) return;
        DiagnosticSummary = "Cancelling the diagnostic run…";
        _diagnosticCancellation?.Cancel();
    }

    private async Task ExportConnectedDiagnosticsAsync()
    {
        if (_diagnosticReport is null) return;
        var suggested = $"RadioVault-{_diagnosticReport.DeviceRole}-{_diagnosticReport.SessionCode}-{DateTime.Now:yyyyMMdd-HHmmss}.trvdiag";
        var path = await _files.PickSaveFileAsync(new FileSelectionRequest(
            Title: "Export Radio Vault diagnostic report",
            Filter: "Radio Vault diagnostics|*.trvdiag|ZIP archives|*.zip",
            DefaultExtension: ".trvdiag",
            SuggestedFileName: suggested,
            CheckFileExists: false)).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;
        await _connectedDiagnostics.ExportAsync(_diagnosticReport, path).ConfigureAwait(true);
        StatusText = $"Diagnostic report exported to {Path.GetFileName(path)}.";
    }

    private async Task CopyConnectedDiagnosticsSummaryAsync()
    {
        var lines = new List<string>
        {
            "Radio Vault connected playback diagnostics",
            $"Session code: {DiagnosticSessionCode}",
            _diagnosticReport?.Summary ?? DiagnosticSummary
        };
        lines.AddRange(DiagnosticSteps.Select(x => $"[{x.Status}] {x.Title} ({x.DurationMs:N0} ms): {x.Message}"));
        await _clipboard.SetTextAsync(string.Join(Environment.NewLine, lines)).ConfigureAwait(true);
        StatusText = "Diagnostic summary copied.";
    }

    private async Task DiagnoseAnywhereAsync()
    {
        await _anywhere.RunDiagnosticsAsync().ConfigureAwait(true);
        AnywhereSnapshot = _anywhere.Current;
        StatusText = AnywhereSnapshot.StatusText;
    }

    private void AnywhereOnStateChanged(object? sender, RadioVaultAnywhereSnapshot snapshot)
    {
        if (_dispatcher.CheckAccess())
        {
            AnywhereSnapshot = snapshot;
            return;
        }
        _ = ApplyAnywhereSnapshotOnUiAsync(snapshot);
    }

    private async Task ApplyAnywhereSnapshotOnUiAsync(RadioVaultAnywhereSnapshot snapshot)
    {
        try { await _dispatcher.InvokeAsync(() => AnywhereSnapshot = snapshot).ConfigureAwait(false); }
        catch { }
    }

    private void ApplyAnywhereSnapshot(RadioVaultAnywhereSnapshot snapshot)
    {
        AnywhereEnabled = snapshot.Enabled;
        AnywhereStartAutomatically = snapshot.StartAutomatically;
        AnywhereServerName = snapshot.ServerDisplayName;
        AnywhereHttpPort = snapshot.HttpPort;
        AnywhereSecureAccess = snapshot.IsSecure || snapshot.HttpsPort > 0;
        AnywhereHttpsPort = snapshot.HttpsPort;
        AnywhereDiscoveryPort = snapshot.DiscoveryPort;
        AnywhereQrCode = PhoneQrCode.Create(snapshot.AccessUrl);
        AnywhereSetupQrCode = PhoneQrCode.Create(snapshot.SetupUrl);
        SelectedPairedClient = snapshot.PairedClients.FirstOrDefault();
    }

    private Task OpenDiagnosticLogAsync()
    {
        if (!File.Exists(_diagnosticLogPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_diagnosticLogPath) ?? _dataDirectory);
            File.WriteAllText(_diagnosticLogPath, "Radio Vault Avalonia diagnostic log has not recorded any entries yet." + Environment.NewLine);
        }
        return _launcher.LaunchAsync(ExternalLaunchRequest.TextFile(_diagnosticLogPath));
    }

    private static JsonObject ReadObject(string path)
    {
        try
        {
            if (!File.Exists(path)) return new JsonObject();
            return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        catch { return new JsonObject(); }
    }

    private static void WriteObject(string path, JsonObject document)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, true);
    }

    private static int Normalise(int value, IReadOnlyCollection<int> allowed, int fallback)
        => allowed.Contains(value) ? value : fallback;

    private void RaiseHealthProperties()
    {
        RaisePropertyChanged(nameof(HasHealthReport));
        RaisePropertyChanged(nameof(HealthScoreText));
        RaisePropertyChanged(nameof(HealthLabel));
        RaisePropertyChanged(nameof(HealthBreakdown));
        RaisePropertyChanged(nameof(HealthIssuesText));
    }

    private void RaiseLibraryScanProperties()
    {
        RaisePropertyChanged(nameof(IsScanRunning));
        RaisePropertyChanged(nameof(LibraryScanStatusText));
        RaisePropertyChanged(nameof(LibraryScanResultText));
        RaisePropertyChanged(nameof(CanScanLibrary));
        RaiseCommandState();
    }

    private void RaiseAnywhereProperties()
    {
        RaisePropertyChanged(nameof(AnywhereCanManage));
        RaisePropertyChanged(nameof(AnywhereAccessUrl));
        RaisePropertyChanged(nameof(AnywhereSetupUrl));
        RaisePropertyChanged(nameof(AnywherePairingCode));
        RaisePropertyChanged(nameof(AnywherePairingExpiryText));
        RaisePropertyChanged(nameof(HasAnywherePairingCode));
        RaisePropertyChanged(nameof(HasAnywhereClients));
        RaisePropertyChanged(nameof(AnywhereClients));
        RaisePropertyChanged(nameof(AnywhereDiagnostics));
        RaisePropertyChanged(nameof(HasAnywhereDiagnostics));
        RaiseCommandState();
    }

    private void RaiseCommandState()
    {
        foreach (var command in new ICommand[]
        {
            RefreshCommand, AddFolderCommand, ToggleFolderCommand, RemoveFolderCommand, AnalyseHealthCommand, ScanLibraryCommand,
            CreateBackupCommand, RestoreBackupCommand, SavePlaybackCommand, SaveTranscriptionCommand,
            BrowseTranscriptionExecutableCommand, BrowseTranscriptionModelCommand, BrowseTranscriptionVadCommand,
            OpenTranscriptionModelsCommand, InstallRecommendedTranscriptionCommand, DownloadTranscriptionWorkerCommand,
            DownloadTranscriptionModelCommand, DownloadTranscriptionVadCommand, SaveAnywhereCommand, StartAnywhereCommand, StopAnywhereCommand,
            DownloadDiarizationModelsCommand,
            GeneratePairingCodeCommand, RevokePairedClientCommand, CopyAnywhereLinkCommand, OpenAnywhereLinkCommand,
            OpenAnywhereSetupCommand, RegenerateAnywhereLinkCommand, ResetAnywhereCertificatesCommand,
            DiagnoseAnywhereCommand, RunQuickDiagnosticsCommand, RunStressDiagnosticsCommand, ExportDiagnosticsCommand, CopyDiagnosticsSummaryCommand, OpenDataFolderCommand, OpenDiagnosticLogCommand
        })
        {
            if (command is AsyncCommand asyncCommand) asyncCommand.RaiseCanExecuteChanged();
        }
        RaiseDiagnosticCommandState();
    }

    private void RaiseDiagnosticCommandState()
    {
        foreach (var command in new[] { RunQuickDiagnosticsCommand, RunStressDiagnosticsCommand, ExportDiagnosticsCommand, CopyDiagnosticsSummaryCommand })
            if (command is AsyncCommand asyncCommand) asyncCommand.RaiseCanExecuteChanged();
        if (CancelDiagnosticsCommand is DelegateCommand cancel) cancel.RaiseCanExecuteChanged();
        if (CancelTranscriptionDownloadCommand is DelegateCommand cancelDownload) cancelDownload.RaiseCanExecuteChanged();
    }

    private void SetError(Exception exception)
    {
        StatusText = exception.Message;
        IsBusy = false;
    }

    public void Dispose()
    {
        _anywhere.StateChanged -= AnywhereOnStateChanged;
        ConnectedAccess.Dispose();
        _diagnosticCancellation?.Cancel();
        _diagnosticCancellation?.Dispose();
        _transcriptionDownloadCancellation?.Cancel();
        _transcriptionDownloadCancellation?.Dispose();
    }
}
