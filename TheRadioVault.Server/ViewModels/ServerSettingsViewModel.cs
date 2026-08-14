using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia.Threading;
using TheRadioVault.Application.Models;
using TheRadioVault.Server.Services;
using TheRadioVault.Services;
using TheRadioVault.Services.Models;
using TheRadioVault.Transcription.Models;
using TheRadioVault.Transcription.Services;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Server.ViewModels;

public sealed partial class ServerSettingsViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly RadioVaultServerRuntime? _runtime;
    private readonly ServerStartupRegistrationService? _startup;
    private readonly ServerFolderSelectionService? _folderSelection;
    private readonly ServerShowSelectionService? _showSelection;
    private readonly ServerKnowledgeFileService? _knowledgeFiles;
    private readonly ServerClipboardService? _clipboard;
    private readonly DispatcherTimer? _refreshTimer;
    private string _serverDisplayName = string.Empty;
    private string _httpPortText = "8765";
    private string _httpsPortText = "8766";
    private bool _enabled;
    private bool _startAutomatically;
    private bool _secureAccessEnabled;
    private bool _lanFederationEnabled;
    private string _lanDiscoveryPortText = "30829";
    private string _statusText = "Starting…";
    private string _detailText = string.Empty;
    private string _accessUrl = string.Empty;
    private string _secureSetupUrl = string.Empty;
    private PhoneQrCode _webQrCode = PhoneQrCode.Empty;
    private PhoneQrCode _secureSetupQrCode = PhoneQrCode.Empty;
    private string _startupStatusText = string.Empty;
    private string _pairingCode = string.Empty;
    private string _pairingExpiryText = string.Empty;
    private PairedClientItem? _selectedPairedClient;
    private string _transcriptionStatusText = "Checking transcription worker...";
    private string _transcriptionDetailText = string.Empty;
    private bool _isTranscriptionBusy;
    private bool _isServerRunning;
    private bool _isServerSecure;
    private bool _isTranscriptionReady;
    private CancellationTokenSource? _transcriptionCancellation;
    private LibraryFolderRecord? _selectedLibraryFolder;
    private bool _isLibraryBusy;
    private string _libraryStatusText = "Loading server Library folders...";
    private bool _isKnowledgeBusy;
    private double _knowledgeProgressPercent;
    private string _knowledgeStatusText = "Choose a Knowledge Database to import, or export this server's complete archive knowledge.";
    private string _knowledgeProgressCountText = string.Empty;
    private Guid? _knowledgeImportSessionId;
    private string? _knowledgeImportPath;
    private WebResearchPackPreview? _knowledgeImportPreview;
    private readonly ServerCommand? _installTranscriptionCommand;
    private readonly ServerCommand? _cancelTranscriptionCommand;
    private readonly ServerCommand? _generatePairingCodeCommand;
    private readonly ServerCommand? _cancelPairingCommand;
    private readonly ServerCommand? _revokeClientCommand;
    private readonly ServerCommand? _revokeAllClientsCommand;
    private readonly ServerCommand? _addLibraryFolderCommand;
    private readonly ServerCommand? _toggleLibraryFolderCommand;
    private readonly ServerCommand? _assignLibraryFolderCommand;
    private readonly ServerCommand? _removeLibraryFolderCommand;
    private readonly ServerCommand? _scanLibraryCommand;
    private readonly ServerCommand? _chooseKnowledgeImportCommand;
    private readonly ServerCommand? _applyKnowledgeImportCommand;
    private readonly ServerCommand? _cancelKnowledgeImportCommand;
    private readonly ServerCommand? _exportKnowledgeCommand;

    public ServerSettingsViewModel(
        RadioVaultServerRuntime runtime,
        ServerFolderSelectionService folderSelection,
        ServerShowSelectionService showSelection,
        ServerKnowledgeFileService knowledgeFiles,
        ServerClipboardService clipboard)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _folderSelection = folderSelection ?? throw new ArgumentNullException(nameof(folderSelection));
        _showSelection = showSelection ?? throw new ArgumentNullException(nameof(showSelection));
        _knowledgeFiles = knowledgeFiles ?? throw new ArgumentNullException(nameof(knowledgeFiles));
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));
        DatabasePath = runtime.DatabasePath;
        _startup = new ServerStartupRegistrationService(DatabasePath);
        SaveCommand = new ServerCommand(Save, () => _runtime is not null);
        StartCommand = new ServerCommand(Start, () => _runtime is not null && !IsServerRunning);
        StopCommand = new ServerCommand(Stop, () => _runtime is not null && IsServerRunning);
        OpenAnywhereCommand = new ServerCommand(OpenAnywhere, () => !string.IsNullOrWhiteSpace(AccessUrl));
        CopyWebLinkCommand = new ServerCommand(() => _ = CopyWebLinkAsync(), () => !string.IsNullOrWhiteSpace(AccessUrl));
        RegenerateWebLinkCommand = new ServerCommand(RegenerateWebLink, () => IsServerRunning);
        OpenSecureSetupCommand = new ServerCommand(OpenSecureSetup, () => !string.IsNullOrWhiteSpace(SecureSetupUrl));
        OpenDataFolderCommand = new ServerCommand(OpenDataFolder, () => _runtime is not null);
        _installTranscriptionCommand = new ServerCommand(
            () => _ = InstallRecommendedTranscriptionAsync(),
            () => _runtime is not null && !IsTranscriptionBusy && !IsTranscriptionReady);
        _cancelTranscriptionCommand = new ServerCommand(CancelTranscriptionDownload, () => IsTranscriptionBusy);
        InstallTranscriptionCommand = _installTranscriptionCommand;
        CancelTranscriptionCommand = _cancelTranscriptionCommand;
        _generatePairingCodeCommand = new ServerCommand(GeneratePairingCode, CanGeneratePairingCode);
        _cancelPairingCommand = new ServerCommand(CancelPairing, () => HasPairingCode);
        _revokeClientCommand = new ServerCommand(RevokeSelectedClient, () => SelectedPairedClient is not null);
        _revokeAllClientsCommand = new ServerCommand(RevokeAllClients, () => HasPairedClients);
        GeneratePairingCodeCommand = _generatePairingCodeCommand;
        CancelPairingCommand = _cancelPairingCommand;
        RevokeClientCommand = _revokeClientCommand;
        RevokeAllClientsCommand = _revokeAllClientsCommand;
        _addLibraryFolderCommand = new ServerCommand(() => _ = AddLibraryFolderAsync(), () => !IsLibraryBusy);
        _toggleLibraryFolderCommand = new ServerCommand(() => _ = ToggleLibraryFolderAsync(), () => !IsLibraryBusy && SelectedLibraryFolder is not null);
        _assignLibraryFolderCommand = new ServerCommand(() => _ = AssignLibraryFolderAsync(), () => !IsLibraryBusy && SelectedLibraryFolder is not null);
        _removeLibraryFolderCommand = new ServerCommand(() => _ = RemoveLibraryFolderAsync(), () => !IsLibraryBusy && SelectedLibraryFolder is not null);
        _scanLibraryCommand = new ServerCommand(() => _ = ScanLibraryAsync(), () => !IsLibraryBusy && HasLibraryFolders);
        AddLibraryFolderCommand = _addLibraryFolderCommand;
        ToggleLibraryFolderCommand = _toggleLibraryFolderCommand;
        AssignLibraryFolderCommand = _assignLibraryFolderCommand;
        RemoveLibraryFolderCommand = _removeLibraryFolderCommand;
        ScanLibraryCommand = _scanLibraryCommand;
        InitializeRssFeedCommands();
        _chooseKnowledgeImportCommand = new ServerCommand(() => _ = ChooseKnowledgeImportAsync(), () => !IsKnowledgeBusy);
        _applyKnowledgeImportCommand = new ServerCommand(() => _ = ApplyKnowledgeImportAsync(), () => HasKnowledgeImportPreview && !IsKnowledgeBusy);
        _cancelKnowledgeImportCommand = new ServerCommand(CancelKnowledgeImport, () => HasKnowledgeImportPreview);
        _exportKnowledgeCommand = new ServerCommand(() => _ = ExportKnowledgeAsync(), () => !IsKnowledgeBusy);
        ChooseKnowledgeImportCommand = _chooseKnowledgeImportCommand;
        ApplyKnowledgeImportCommand = _applyKnowledgeImportCommand;
        CancelKnowledgeImportCommand = _cancelKnowledgeImportCommand;
        ExportKnowledgeCommand = _exportKnowledgeCommand;
        LoadPreferences();
        RefreshStatus();
        _ = LoadLibraryFoldersAsync();
        _ = LoadRssFeedsAsync();
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshStatus());
        _refreshTimer.Start();
    }

    public ServerSettingsViewModel(Exception exception)
    {
        DatabasePath = AppPaths.DatabasePath;
        StatusText = "Server could not start";
        DetailText = exception.Message;
        SaveCommand = StartCommand = StopCommand = OpenAnywhereCommand = CopyWebLinkCommand = RegenerateWebLinkCommand = OpenSecureSetupCommand =
            OpenDataFolderCommand = InstallTranscriptionCommand = CancelTranscriptionCommand =
            GeneratePairingCodeCommand = CancelPairingCommand = RevokeClientCommand = RevokeAllClientsCommand = AddLibraryFolderCommand =
            ToggleLibraryFolderCommand = AssignLibraryFolderCommand = RemoveLibraryFolderCommand = ScanLibraryCommand =
            ChooseKnowledgeImportCommand = ApplyKnowledgeImportCommand = CancelKnowledgeImportCommand = ExportKnowledgeCommand =
                new ServerCommand(() => { }, () => false);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string VersionText => $"Radio Vault Server {typeof(ServerSettingsViewModel).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.34"}";
    public string DatabasePath { get; }
    public string ServerDisplayName { get => _serverDisplayName; set => Set(ref _serverDisplayName, value); }
    public string HttpPortText { get => _httpPortText; set => Set(ref _httpPortText, value); }
    public string HttpsPortText { get => _httpsPortText; set => Set(ref _httpsPortText, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    public bool StartAutomatically { get => _startAutomatically; set => Set(ref _startAutomatically, value); }
    public string StartAutomaticallyLabel => _startup?.SettingLabel ?? "Start automatically";
    public bool IsAutomaticTranscriptionSetupSupported => OperatingSystem.IsWindows();
    public bool ShowAutomaticTranscriptionSetup => IsAutomaticTranscriptionSetupSupported && IsTranscriptionSetupRequired;
    public string TranscriptionPlatformGuidance => OperatingSystem.IsWindows()
        ? string.Empty
        : "Automatic Whisper installation is not yet available on this platform. You can still configure a locally installed whisper.cpp worker.";
    public bool SecureAccessEnabled { get => _secureAccessEnabled; set => Set(ref _secureAccessEnabled, value); }
    public bool LanFederationEnabled { get => _lanFederationEnabled; set => Set(ref _lanFederationEnabled, value); }
    public string LanDiscoveryPortText { get => _lanDiscoveryPortText; set => Set(ref _lanDiscoveryPortText, value); }
    public string StartupStatusText { get => _startupStatusText; private set => Set(ref _startupStatusText, value); }
    public string PairingCode { get => _pairingCode; private set { if (Set(ref _pairingCode, value)) RaisePairingState(); } }
    public string PairingExpiryText { get => _pairingExpiryText; private set => Set(ref _pairingExpiryText, value); }
    public bool HasPairingCode => !string.IsNullOrWhiteSpace(PairingCode);
    public bool HasPairedClients => PairedClients.Count > 0;
    public ObservableCollection<PairedClientItem> PairedClients { get; } = new();
    public ObservableCollection<LibraryFolderRecord> LibraryFolders { get; } = new();
    public bool HasLibraryFolders => LibraryFolders.Count > 0;
    public bool IsKnowledgeBusy
    {
        get => _isKnowledgeBusy;
        private set
        {
            if (!Set(ref _isKnowledgeBusy, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsKnowledgeIdle)));
            RaiseKnowledgeCommandState();
        }
    }
    public bool IsKnowledgeIdle => !IsKnowledgeBusy;
    public bool HasKnowledgeImportPreview => _knowledgeImportPreview is not null && _knowledgeImportSessionId.HasValue;
    public double KnowledgeProgressPercent
    {
        get => _knowledgeProgressPercent;
        private set
        {
            if (!Set(ref _knowledgeProgressPercent, Math.Clamp(value, 0, 100))) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KnowledgeProgressPercentText)));
        }
    }
    public string KnowledgeProgressPercentText => $"{KnowledgeProgressPercent:0}%";
    public string KnowledgeStatusText { get => _knowledgeStatusText; private set => Set(ref _knowledgeStatusText, value); }
    public string KnowledgeProgressCountText { get => _knowledgeProgressCountText; private set => Set(ref _knowledgeProgressCountText, value); }
    public string KnowledgeImportFileText => string.IsNullOrWhiteSpace(_knowledgeImportPath)
        ? string.Empty
        : Path.GetFileName(_knowledgeImportPath);
    public string KnowledgeImportPreviewText => _knowledgeImportPreview is null
        ? string.Empty
        : $"{_knowledgeImportPreview.TotalRecords:N0} records · {_knowledgeImportPreview.ExactMatches:N0} matched · " +
          $"{_knowledgeImportPreview.MissingRecords:N0} missing · {_knowledgeImportPreview.AmbiguousMatches:N0} need review · " +
          $"{_knowledgeImportPreview.WikiPageCount:N0} Explore pages · {_knowledgeImportPreview.WikiImageCount:N0} images";
    public LibraryFolderRecord? SelectedLibraryFolder
    {
        get => _selectedLibraryFolder;
        set
        {
            if (!Set(ref _selectedLibraryFolder, value)) return;
            _toggleLibraryFolderCommand?.RaiseCanExecuteChanged();
            _assignLibraryFolderCommand?.RaiseCanExecuteChanged();
            _removeLibraryFolderCommand?.RaiseCanExecuteChanged();
        }
    }
    public bool IsLibraryBusy
    {
        get => _isLibraryBusy;
        private set
        {
            if (!Set(ref _isLibraryBusy, value)) return;
            RaiseLibraryCommandState();
        }
    }
    public string LibraryStatusText { get => _libraryStatusText; private set => Set(ref _libraryStatusText, value); }
    public PairedClientItem? SelectedPairedClient
    {
        get => _selectedPairedClient;
        set
        {
            if (!Set(ref _selectedPairedClient, value)) return;
            _revokeClientCommand?.RaiseCanExecuteChanged();
        }
    }
    public string TranscriptionStatusText { get => _transcriptionStatusText; private set => Set(ref _transcriptionStatusText, value); }
    public string TranscriptionDetailText { get => _transcriptionDetailText; private set => Set(ref _transcriptionDetailText, value); }
    public bool IsServerRunning
    {
        get => _isServerRunning;
        private set
        {
            if (!Set(ref _isServerRunning, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsServerStopped)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ServerStateLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ServerStateBrush)));
            (StartCommand as ServerCommand)?.RaiseCanExecuteChanged();
            (StopCommand as ServerCommand)?.RaiseCanExecuteChanged();
            (RegenerateWebLinkCommand as ServerCommand)?.RaiseCanExecuteChanged();
        }
    }
    public bool IsServerStopped => !IsServerRunning;
    public bool IsServerSecure
    {
        get => _isServerSecure;
        private set
        {
            if (!Set(ref _isServerSecure, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ServerStateLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ServerStateBrush)));
        }
    }
    public string ServerStateLabel => !IsServerRunning ? "STOPPED" : IsServerSecure ? "RUNNING SECURELY" : "RUNNING — HTTP";
    public string ServerStateBrush => !IsServerRunning ? "#E3656B" : IsServerSecure ? "#52D6A2" : "#E2A84A";
    public bool IsTranscriptionReady
    {
        get => _isTranscriptionReady;
        private set
        {
            if (!Set(ref _isTranscriptionReady, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTranscriptionSetupRequired)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAutomaticTranscriptionSetup)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranscriptionStateLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranscriptionStateBrush)));
            _installTranscriptionCommand?.RaiseCanExecuteChanged();
        }
    }
    public bool IsTranscriptionSetupRequired => !IsTranscriptionReady && !IsTranscriptionBusy;
    public string TranscriptionStateLabel => IsTranscriptionBusy ? "INSTALLING" : IsTranscriptionReady ? "READY" : "SETUP REQUIRED";
    public string TranscriptionStateBrush => IsTranscriptionBusy ? "#E2A84A" : IsTranscriptionReady ? "#52D6A2" : "#E3656B";
    public bool IsTranscriptionBusy
    {
        get => _isTranscriptionBusy;
        private set
        {
            if (!Set(ref _isTranscriptionBusy, value)) return;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTranscriptionSetupRequired)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ShowAutomaticTranscriptionSetup)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranscriptionStateLabel)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TranscriptionStateBrush)));
            _installTranscriptionCommand?.RaiseCanExecuteChanged();
            _cancelTranscriptionCommand?.RaiseCanExecuteChanged();
        }
    }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string DetailText { get => _detailText; private set => Set(ref _detailText, value); }
    public string AccessUrl
    {
        get => _accessUrl;
        private set
        {
            if (!Set(ref _accessUrl, value)) return;
            WebQrCode = PhoneQrCode.Create(value);
            (OpenAnywhereCommand as ServerCommand)?.RaiseCanExecuteChanged();
            (CopyWebLinkCommand as ServerCommand)?.RaiseCanExecuteChanged();
        }
    }
    public string SecureSetupUrl
    {
        get => _secureSetupUrl;
        private set
        {
            if (!Set(ref _secureSetupUrl, value)) return;
            SecureSetupQrCode = PhoneQrCode.Create(value);
            (OpenSecureSetupCommand as ServerCommand)?.RaiseCanExecuteChanged();
        }
    }
    public PhoneQrCode WebQrCode { get => _webQrCode; private set => Set(ref _webQrCode, value); }
    public PhoneQrCode SecureSetupQrCode { get => _secureSetupQrCode; private set => Set(ref _secureSetupQrCode, value); }

    public ICommand SaveCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand OpenAnywhereCommand { get; }
    public ICommand CopyWebLinkCommand { get; }
    public ICommand RegenerateWebLinkCommand { get; }
    public ICommand OpenSecureSetupCommand { get; }
    public ICommand OpenDataFolderCommand { get; }
    public ICommand InstallTranscriptionCommand { get; }
    public ICommand CancelTranscriptionCommand { get; }
    public ICommand GeneratePairingCodeCommand { get; }
    public ICommand CancelPairingCommand { get; }
    public ICommand RevokeClientCommand { get; }
    public ICommand RevokeAllClientsCommand { get; }
    public ICommand AddLibraryFolderCommand { get; }
    public ICommand ToggleLibraryFolderCommand { get; }
    public ICommand AssignLibraryFolderCommand { get; }
    public ICommand RemoveLibraryFolderCommand { get; }
    public ICommand ScanLibraryCommand { get; }
    public ICommand ChooseKnowledgeImportCommand { get; }
    public ICommand ApplyKnowledgeImportCommand { get; }
    public ICommand CancelKnowledgeImportCommand { get; }
    public ICommand ExportKnowledgeCommand { get; }

    private async Task ChooseKnowledgeImportAsync()
    {
        if (_runtime is null || _knowledgeFiles is null) return;
        var path = await _knowledgeFiles.PickImportAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;
        IsKnowledgeBusy = true;
        KnowledgeProgressPercent = 0;
        KnowledgeProgressCountText = string.Empty;
        KnowledgeStatusText = "Reading the Knowledge Database…";
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) throw new FileNotFoundException("The selected Knowledge Database no longer exists.", path);
            if (file.Length == 0) throw new InvalidDataException("The selected Knowledge Database is empty.");
            if (file.Length > WebResearchPackLimits.MaximumPackageBytes)
                throw new InvalidDataException($"Knowledge Databases are limited to {WebResearchPackLimits.MaximumPackageBytes / 1024 / 1024} MB.");
            KnowledgeProgressPercent = 15;
            KnowledgeStatusText = "Comparing the Knowledge Database with the server archive…";
            var preview = await _runtime.PreviewKnowledgeDatabaseFileAsync(file.FullName, file.Name).ConfigureAwait(true);
            _knowledgeImportPath = path;
            _knowledgeImportSessionId = preview.SessionId;
            _knowledgeImportPreview = preview.Preview;
            KnowledgeProgressPercent = 100;
            KnowledgeProgressCountText = $"{preview.Preview.TotalRecords:N0} records";
            KnowledgeStatusText = "Knowledge Database checked and ready to import.";
            RaiseKnowledgePreviewState();
        }
        catch (Exception exception)
        {
            _knowledgeImportPath = null;
            _knowledgeImportSessionId = null;
            _knowledgeImportPreview = null;
            KnowledgeStatusText = exception.Message;
            RaiseKnowledgePreviewState();
        }
        finally
        {
            IsKnowledgeBusy = false;
        }
    }

    private async Task ApplyKnowledgeImportAsync()
    {
        if (_runtime is null || !_knowledgeImportSessionId.HasValue) return;
        var sessionId = _knowledgeImportSessionId.Value;
        IsKnowledgeBusy = true;
        KnowledgeProgressPercent = 0;
        KnowledgeStatusText = "Starting the server-owned import job…";
        try
        {
            var job = _runtime.StartKnowledgeDatabaseImport(sessionId);
            while (job.State is "Queued" or "Running" or "Pending")
            {
                UpdateKnowledgeJob(job);
                await Task.Delay(TimeSpan.FromMilliseconds(750)).ConfigureAwait(true);
                job = _runtime.GetKnowledgeDatabaseImportStatus(sessionId);
            }
            UpdateKnowledgeJob(job);
            if (job.State == "Failed")
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(job.Error)
                    ? "The Knowledge Database import failed. No partial changes were kept."
                    : $"{job.Error} No partial changes were kept.");
            if (job.State == "Cancelled")
            {
                KnowledgeStatusText = "Knowledge Database import cancelled. No partial changes were kept.";
                _knowledgeImportPath = null;
                _knowledgeImportSessionId = null;
                _knowledgeImportPreview = null;
                RaiseKnowledgePreviewState();
                return;
            }

            var result = job.Result ?? throw new InvalidOperationException("The completed import did not return a result.");
            KnowledgeProgressPercent = 100;
            KnowledgeProgressCountText = $"{result.ResearchRecordsStored:N0} records";
            KnowledgeStatusText = $"Import complete: {result.ResearchRecordsStored:N0} Knowledge records and {result.WikiPagesChanged:N0} Explore pages stored.";
            _knowledgeImportPath = null;
            _knowledgeImportSessionId = null;
            _knowledgeImportPreview = null;
            RaiseKnowledgePreviewState();
        }
        catch (Exception exception)
        {
            KnowledgeStatusText = exception.Message;
        }
        finally
        {
            IsKnowledgeBusy = false;
        }
    }

    private void CancelKnowledgeImport()
    {
        if (_runtime is null || !_knowledgeImportSessionId.HasValue) return;
        _runtime.CancelKnowledgeDatabaseImport(_knowledgeImportSessionId.Value);
        KnowledgeStatusText = IsKnowledgeBusy
            ? "Cancelling the Knowledge Database import; the active transaction will roll back…"
            : "Knowledge Database import cancelled.";
        if (!IsKnowledgeBusy)
        {
            _knowledgeImportPath = null;
            _knowledgeImportSessionId = null;
            _knowledgeImportPreview = null;
            RaiseKnowledgePreviewState();
        }
    }

    private async Task ExportKnowledgeAsync()
    {
        if (_runtime is null || _knowledgeFiles is null) return;
        var path = await _knowledgeFiles.PickExportAsync("RadioVault-Archive-Knowledge.trvknowledge").ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;
        IsKnowledgeBusy = true;
        KnowledgeProgressPercent = 5;
        KnowledgeProgressCountText = string.Empty;
        KnowledgeStatusText = "Building the complete Archive Knowledge Database…";
        try
        {
            var export = await _runtime.ExportKnowledgeDatabaseAsync().ConfigureAwait(true);
            KnowledgeProgressPercent = 90;
            KnowledgeStatusText = "Writing the portable Knowledge Database…";
            await File.WriteAllBytesAsync(path, export.Bytes).ConfigureAwait(true);
            KnowledgeProgressPercent = 100;
            KnowledgeProgressCountText = $"{export.BroadcastCount:N0} broadcasts · {export.WikiPageCount:N0} Explore pages";
            KnowledgeStatusText = $"Knowledge Database exported to {path}";
        }
        catch (Exception exception)
        {
            KnowledgeStatusText = exception.Message;
        }
        finally
        {
            IsKnowledgeBusy = false;
        }
    }

    private void UpdateKnowledgeJob(WebResearchPackImportJob job)
    {
        KnowledgeProgressPercent = job.Percent;
        KnowledgeStatusText = job.Message;
        KnowledgeProgressCountText = job.Total > 0 ? $"{job.Current:N0} of {job.Total:N0}" : string.Empty;
    }

    private void RaiseKnowledgePreviewState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasKnowledgeImportPreview)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KnowledgeImportFileText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(KnowledgeImportPreviewText)));
        RaiseKnowledgeCommandState();
    }

    private void RaiseKnowledgeCommandState()
    {
        _chooseKnowledgeImportCommand?.RaiseCanExecuteChanged();
        _applyKnowledgeImportCommand?.RaiseCanExecuteChanged();
        _cancelKnowledgeImportCommand?.RaiseCanExecuteChanged();
        _exportKnowledgeCommand?.RaiseCanExecuteChanged();
    }

    private async Task LoadLibraryFoldersAsync()
    {
        if (_runtime is null) return;
        try
        {
            var folders = await _runtime.GetLibraryFoldersAsync().ConfigureAwait(true);
            LibraryFolders.Clear();
            foreach (var folder in folders) LibraryFolders.Add(folder);
            RefreshRssDestinationFolders();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasLibraryFolders)));
            LibraryStatusText = folders.Count == 0
                ? "No Library folders are registered. Add the main archive folder from this computer."
                : $"{folders.Count:N0} server Library folder{(folders.Count == 1 ? string.Empty : "s")} registered.";
            RaiseLibraryCommandState();
        }
        catch (Exception exception)
        {
            LibraryStatusText = exception.Message;
        }
    }

    private async Task AddLibraryFolderAsync()
    {
        if (_runtime is null || _folderSelection is null || _showSelection is null) return;
        var path = await _folderSelection.PickLibraryFolderAsync().ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Directory.Exists(path))
        {
            LibraryStatusText = "The selected folder is not currently available on the server computer.";
            return;
        }
        var selection = await ChooseLibraryFolderShowAsync(path, isExistingFolder: false).ConfigureAwait(true);
        if (selection is null) return;
        IsLibraryBusy = true;
        LibraryStatusText = "Registering the server Library folder...";
        try
        {
            await _runtime.AddLibraryFolderAsync(path, selection.CollectionId).ConfigureAwait(true);
            await LoadLibraryFoldersAsync().ConfigureAwait(true);
            LibraryStatusText = selection.CollectionId.HasValue
                ? $"Library folder registered as {selection.Name}. Choose Scan Library now to import its broadcasts."
                : "Mixed-show folder registered with automatic detection. Choose Scan Library now to import its broadcasts.";
        }
        catch (Exception exception) { LibraryStatusText = exception.Message; }
        finally { IsLibraryBusy = false; }
    }

    private async Task AssignLibraryFolderAsync()
    {
        if (_runtime is null || _showSelection is null || SelectedLibraryFolder is null) return;
        var selected = SelectedLibraryFolder;
        var selection = await ChooseLibraryFolderShowAsync(selected.Path, isExistingFolder: true).ConfigureAwait(true);
        if (selection is null) return;

        IsLibraryBusy = true;
        LibraryStatusText = $"Changing the folder to {selection.Name} and rescanning its broadcasts...";
        try
        {
            await _runtime.SetLibraryFolderCollectionAsync(selected.Id, selection.CollectionId).ConfigureAwait(true);
            var result = await _runtime.ScanLibraryAsync().ConfigureAwait(true);
            await LoadLibraryFoldersAsync().ConfigureAwait(true);
            LibraryStatusText = $"Folder assigned to {selection.Name}. {result.Message}";
        }
        catch (Exception exception) { LibraryStatusText = exception.Message; }
        finally { IsLibraryBusy = false; }
    }

    private async Task<LibraryFolderShowChoice?> ChooseLibraryFolderShowAsync(string path, bool isExistingFolder)
    {
        if (_runtime is null || _showSelection is null) return null;
        try
        {
            var choices = new List<LibraryFolderShowChoice>
            {
                new(
                    CollectionId: null,
                    Name: "Auto-detect / mixed-show folder",
                    Description: "Classify each recording from its folder path and filename.")
            };
            choices.AddRange((await _runtime.GetAssignableLibraryCollectionsAsync().ConfigureAwait(true))
                .Select(collection => new LibraryFolderShowChoice(
                    collection.CollectionId,
                    collection.Name,
                    $"Treat recordings in this folder as {collection.Name} unless a more specific filename rule applies.")));
            return await _showSelection.ChooseAsync(path, choices, isExistingFolder).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            LibraryStatusText = exception.Message;
            return null;
        }
    }

    private async Task ToggleLibraryFolderAsync()
    {
        if (_runtime is null || SelectedLibraryFolder is null) return;
        var selected = SelectedLibraryFolder;
        IsLibraryBusy = true;
        try
        {
            await _runtime.SetLibraryFolderEnabledAsync(selected.Id, !selected.Enabled).ConfigureAwait(true);
            await LoadLibraryFoldersAsync().ConfigureAwait(true);
            LibraryStatusText = selected.Enabled ? "Library folder disabled." : "Library folder enabled.";
        }
        catch (Exception exception) { LibraryStatusText = exception.Message; }
        finally { IsLibraryBusy = false; }
    }

    private async Task RemoveLibraryFolderAsync()
    {
        if (_runtime is null || SelectedLibraryFolder is null) return;
        var selected = SelectedLibraryFolder;
        IsLibraryBusy = true;
        try
        {
            await _runtime.RemoveLibraryFolderAsync(selected.Id).ConfigureAwait(true);
            SelectedLibraryFolder = null;
            await LoadLibraryFoldersAsync().ConfigureAwait(true);
            LibraryStatusText = "Library folder registration removed. No audio files were deleted.";
        }
        catch (Exception exception) { LibraryStatusText = exception.Message; }
        finally { IsLibraryBusy = false; }
    }

    private async Task ScanLibraryAsync()
    {
        if (_runtime is null) return;
        IsLibraryBusy = true;
        LibraryStatusText = "Scanning registered server Library folders...";
        try
        {
            var result = await _runtime.ScanLibraryAsync().ConfigureAwait(true);
            LibraryStatusText = result.Message;
            await LoadLibraryFoldersAsync().ConfigureAwait(true);
            LibraryStatusText = result.Message;
        }
        catch (Exception exception) { LibraryStatusText = exception.Message; }
        finally { IsLibraryBusy = false; }
    }

    private void RaiseLibraryCommandState()
    {
        _addLibraryFolderCommand?.RaiseCanExecuteChanged();
        _toggleLibraryFolderCommand?.RaiseCanExecuteChanged();
        _assignLibraryFolderCommand?.RaiseCanExecuteChanged();
        _removeLibraryFolderCommand?.RaiseCanExecuteChanged();
        _scanLibraryCommand?.RaiseCanExecuteChanged();
    }

    private void LoadPreferences()
    {
        var preferences = _runtime!.Preferences;
        ServerDisplayName = preferences.ServerDisplayName;
        HttpPortText = preferences.Port.ToString();
        HttpsPortText = preferences.SecurePort.ToString();
        Enabled = preferences.Enabled;
        StartAutomatically = preferences.StartAutomatically;
        SecureAccessEnabled = preferences.SecureAccessEnabled;
        LanFederationEnabled = preferences.LanFederationEnabled;
        LanDiscoveryPortText = preferences.LanDiscoveryPort.ToString();
        StartupStatusText = _startup?.StatusText ?? "Automatic startup is unavailable.";
    }

    private void Save()
    {
        if (_runtime is null) return;
        try
        {
            var preferences = _runtime.Preferences;
            preferences.ServerDisplayName = string.IsNullOrWhiteSpace(ServerDisplayName)
                ? $"Radio Vault on {Environment.MachineName}"
                : ServerDisplayName.Trim();
            preferences.Port = ReadPort(HttpPortText, "HTTP");
            preferences.SecurePort = ReadPort(HttpsPortText, "HTTPS");
            preferences.StartAutomatically = StartAutomatically;
            preferences.SecureAccessEnabled = SecureAccessEnabled;
            preferences.LanFederationEnabled = LanFederationEnabled;
            preferences.LanDiscoveryPort = ReadPort(LanDiscoveryPortText, "Discovery");
            if (preferences.LanFederationEnabled && !preferences.SecureAccessEnabled)
                throw new InvalidOperationException("Secure HTTPS access is required before enabling native remote clients.");
            _runtime.Apply(preferences, Enabled);
            StartupStatusText = _startup?.SetEnabled(StartAutomatically) ?? string.Empty;
            DetailText = "Server settings and background startup were saved.";
        }
        catch (Exception exception)
        {
            DetailText = exception.Message;
        }
        RefreshStatus();
    }

    private void Start()
    {
        if (_runtime is null) return;
        try
        {
            _runtime.Start();
            Enabled = true;
            _runtime.Preferences.Enabled = true;
            _runtime.Preferences.Save();
            DetailText = "RadioVault Server started.";
        }
        catch (Exception exception) { DetailText = exception.Message; }
        RefreshStatus();
    }

    private void Stop()
    {
        if (_runtime is null) return;
        _runtime.Stop();
        Enabled = false;
        _runtime.Preferences.Enabled = false;
        _runtime.Preferences.Save();
        DetailText = "RadioVault Server stopped.";
        RefreshStatus();
    }

    private void OpenAnywhere()
    {
        if (string.IsNullOrWhiteSpace(AccessUrl)) return;
        Process.Start(new ProcessStartInfo(AccessUrl) { UseShellExecute = true });
    }

    private async Task CopyWebLinkAsync()
    {
        if (_clipboard is null || string.IsNullOrWhiteSpace(AccessUrl)) return;
        try
        {
            await _clipboard.SetTextAsync(AccessUrl).ConfigureAwait(true);
            DetailText = "Private Radio Vault Web link copied.";
        }
        catch (Exception exception) { DetailText = exception.Message; }
    }

    private void RegenerateWebLink()
    {
        if (_runtime is null || !IsServerRunning) return;
        try
        {
            _runtime.RegenerateWebAccessToken();
            RefreshStatus();
            DetailText = "A new private Radio Vault Web link was created. Previous Web links no longer work.";
        }
        catch (Exception exception) { DetailText = exception.Message; }
    }

    private void OpenSecureSetup()
    {
        if (string.IsNullOrWhiteSpace(SecureSetupUrl)) return;
        Process.Start(new ProcessStartInfo(SecureSetupUrl) { UseShellExecute = true });
    }

    private void OpenDataFolder()
    {
        var directory = Path.GetDirectoryName(DatabasePath) ?? AppPaths.DataDirectory;
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void RefreshStatus()
    {
        if (_runtime is null) return;
        IsServerRunning = _runtime.IsRunning;
        IsServerSecure = _runtime.IsRunning && _runtime.IsSecure;
        StatusText = _runtime.IsRunning
            ? _runtime.IsSecure ? "Running securely" : "Running over HTTP"
            : "Stopped";
        AccessUrl = _runtime.AccessUrls.FirstOrDefault() ?? string.Empty;
        SecureSetupUrl = _runtime.SecureSetupUrls.FirstOrDefault() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(_runtime.LastError)) DetailText = _runtime.LastError!;
        var transcription = _runtime.TranscriptionStatus;
        if (!IsTranscriptionBusy)
        {
            IsTranscriptionReady = transcription.IsAvailable && transcription.DiarizationAvailable;
            TranscriptionStatusText = transcription.IsAvailable ? "Ready" : "Setup required";
            TranscriptionDetailText = transcription.IsAvailable
                ? transcription.AvailabilityMessage + (transcription.DiarizationAvailable ? " Multi-speaker diarization is ready." : "")
                : transcription.AvailabilityMessage;
        }
        var pairing = _runtime.CurrentDesktopPairing;
        PairingCode = pairing?.Code ?? string.Empty;
        PairingExpiryText = pairing is null
            ? "Create a six-digit code when the remote client is ready."
            : $"Expires in {Math.Max(0, (int)Math.Ceiling((pairing.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds))} seconds.";
        RefreshPairedClients();
        RaisePairingState();
    }

    private bool CanGeneratePairingCode()
        => _runtime?.IsRunning == true && _runtime.IsSecure && _runtime.Preferences.LanFederationEnabled;

    private void GeneratePairingCode()
    {
        if (_runtime is null) return;
        try
        {
            var pairing = _runtime.BeginDesktopPairing();
            PairingCode = pairing.Code;
            PairingExpiryText = "Expires in 300 seconds.";
            DetailText = "Enter this code on the remote Radio Vault client. It can be used once.";
        }
        catch (Exception exception) { DetailText = exception.Message; }
        RaisePairingState();
    }

    private void CancelPairing()
    {
        _runtime?.CancelDesktopPairing();
        PairingCode = string.Empty;
        PairingExpiryText = "The pairing code was cancelled.";
        DetailText = "No new client was trusted.";
    }

    private void RevokeSelectedClient()
    {
        if (_runtime is null || SelectedPairedClient is null) return;
        var name = SelectedPairedClient.DisplayName;
        _runtime.RevokeDesktopClient(SelectedPairedClient.ClientId);
        SelectedPairedClient = null;
        RefreshPairedClients();
        DetailText = $"Access was revoked for {name}.";
    }

    private void RevokeAllClients()
    {
        if (_runtime is null) return;
        var count = _runtime.RevokeAllDesktopClients();
        SelectedPairedClient = null;
        RefreshPairedClients();
        DetailText = count == 0
            ? "No remote clients were paired."
            : $"Access was revoked for {count} remote client{(count == 1 ? string.Empty : "s")}.";
    }

    private void RefreshPairedClients()
    {
        if (_runtime is null) return;
        var existing = PairedClients.Select(item => item.ClientId).ToArray();
        var incoming = _runtime.PairedDesktopClients.Select(item => item.ClientId).ToArray();
        if (existing.SequenceEqual(incoming, StringComparer.Ordinal)) return;
        PairedClients.Clear();
        foreach (var client in _runtime.PairedDesktopClients)
            PairedClients.Add(new PairedClientItem(client.ClientId, client.DisplayName, client.PairedAt));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPairedClients)));
        _revokeAllClientsCommand?.RaiseCanExecuteChanged();
    }

    private void RaisePairingState()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPairingCode)));
        _generatePairingCodeCommand?.RaiseCanExecuteChanged();
        _cancelPairingCommand?.RaiseCanExecuteChanged();
    }

    private async Task InstallRecommendedTranscriptionAsync()
    {
        if (_runtime is null || IsTranscriptionBusy) return;
        _transcriptionCancellation?.Dispose();
        _transcriptionCancellation = new CancellationTokenSource();
        IsTranscriptionBusy = true;
        var progress = new Progress<WhisperDownloadProgress>(update =>
        {
            TranscriptionStatusText = update.Percent.HasValue ? $"Installing - {update.Percent:0}%" : "Installing";
            TranscriptionDetailText = update.Message;
        });
        try
        {
            var token = _transcriptionCancellation.Token;
            var downloads = _runtime.Transcription.Downloads;
            var worker = await downloads.InstallLatestWindowsWorkerAsync(progress, token);
            var model = WhisperModelCatalog.Items.First(x => x.Id == "base.en");
            var modelPath = await downloads.DownloadModelAsync(model, progress, token);
            var vadPath = await downloads.DownloadVadModelAsync(progress, token);
            var diarization = await downloads.DownloadDiarizationModelsAsync(progress, token);
            var settings = _runtime.Transcription.GetSettings();
            settings.ExecutablePath = worker.ExecutablePath;
            settings.ModelPath = modelPath;
            settings.VadModelPath = vadPath;
            settings.DiarizationSegmentationModelPath = diarization.SegmentationModelPath;
            settings.DiarizationEmbeddingModelPath = diarization.EmbeddingModelPath;
            settings.UseVoiceActivityDetection = true;
            settings.EnableMultiSpeakerDiarization = true;
            _runtime.Transcription.SaveSettings(settings);
            TranscriptionStatusText = "Ready";
            TranscriptionDetailText = "Whisper, VAD and multi-speaker diarization are installed and owned by this server.";
        }
        catch (OperationCanceledException)
        {
            TranscriptionStatusText = "Setup cancelled";
            TranscriptionDetailText = "Existing transcription files and settings were left unchanged.";
        }
        catch (Exception exception)
        {
            TranscriptionStatusText = "Setup failed";
            TranscriptionDetailText = exception.Message;
        }
        finally
        {
            IsTranscriptionBusy = false;
            _transcriptionCancellation?.Dispose();
            _transcriptionCancellation = null;
        }
    }

    private void CancelTranscriptionDownload()
    {
        _transcriptionCancellation?.Cancel();
        TranscriptionDetailText = "Cancelling transcription setup...";
    }

    private static int ReadPort(string text, string label)
    {
        if (!int.TryParse(text, out var value) || value is < 1024 or > 65535)
            throw new InvalidOperationException($"{label} port must be between 1024 and 65535.");
        return value;
    }

    public void Dispose()
    {
        _refreshTimer?.Stop();
        _transcriptionCancellation?.Cancel();
        _transcriptionCancellation?.Dispose();
        _transcriptionCancellation = null;
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    private sealed class ServerCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;
        public ServerCommand(Action execute, Func<bool> canExecute) { _execute = execute; _canExecute = canExecute; }
        public event EventHandler? CanExecuteChanged;
        public bool CanExecute(object? parameter) => _canExecute();
        public void Execute(object? parameter) => _execute();
        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}

public sealed record PairedClientItem(string ClientId, string DisplayName, DateTimeOffset PairedAt)
{
    public string DetailText => $"Paired {PairedAt.LocalDateTime:g} · {ClientId}";
}
