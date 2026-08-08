using System.Collections.ObjectModel;
using System.Windows.Input;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class DownloadsViewModel : ObservableObject, IDisposable
{
    private readonly INativeDownloadService _downloads;
    private readonly PlaybackViewModel _playback;
    private readonly IUiDispatcher _dispatcher;
    private readonly IUserNotificationService _notifications;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly HashSet<long> _downloadedIds = new();
    private CancellationTokenSource? _activeDownload;
    private bool _isBusy;
    private bool _isDownloading;
    private bool _isLoaded;
    private string _statusText = "Downloads have not been checked yet.";
    private string _activeTitle = string.Empty;
    private string _activeDetail = string.Empty;
    private int _downloadPercent;
    private long _storedBytes;
    private int _repairCount;
    private bool _disposed;

    public DownloadsViewModel(
        INativeDownloadService downloads,
        PlaybackViewModel playback,
        IUiDispatcher dispatcher,
        IUserNotificationService notifications)
    {
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        RefreshCommand = new AsyncCommand(() => LoadAsync(force: true), () => !IsBusy, SetError);
        AuditCommand = new AsyncCommand(AuditAsync, () => !IsBusy, SetError);
        CancelDownloadCommand = new DelegateCommand(CancelDownload, () => IsDownloading);
        RemoveAllCommand = new AsyncCommand(RemoveAllAsync, () => !IsBusy && HasDownloads, SetError);
        _downloads.DownloadsChanged += DownloadsOnChanged;
    }

    public ObservableCollection<NativeDownloadRowViewModel> Items { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand AuditCommand { get; }
    public ICommand CancelDownloadCommand { get; }
    public ICommand RemoveAllCommand { get; }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandState(); } }
    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (!SetProperty(ref _isDownloading, value)) return;
            RaisePropertyChanged(nameof(IsNotDownloading));
            RaiseCommandState();
        }
    }
    public bool IsNotDownloading => !IsDownloading;
    public bool HasDownloads => Items.Count > 0;
    public bool HasNoDownloads => !HasDownloads;
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ActiveTitle { get => _activeTitle; private set => SetProperty(ref _activeTitle, value); }
    public string ActiveDetail { get => _activeDetail; private set => SetProperty(ref _activeDetail, value); }
    public int DownloadPercent { get => _downloadPercent; private set => SetProperty(ref _downloadPercent, value); }
    public long StoredBytes { get => _storedBytes; private set { if (SetProperty(ref _storedBytes, value)) RaisePropertyChanged(nameof(StoredSizeText)); } }
    public string StoredSizeText => FormatBytes(StoredBytes);
    public int RepairCount { get => _repairCount; private set { if (SetProperty(ref _repairCount, value)) RaisePropertyChanged(nameof(HealthText)); } }
    public string HealthText => RepairCount == 0 ? "All downloads checked" : $"{RepairCount:N0} need repair";
    public string CountText => $"{Items.Count:N0} {(Items.Count == 1 ? "broadcast" : "broadcasts")}";

    public event EventHandler? DownloadsChanged;

    public bool IsDownloaded(long representativeEpisodeId) => _downloadedIds.Contains(representativeEpisodeId);

    public async Task LoadAsync(bool force = false)
    {
        if (_isLoaded && !force) return;
        if (!await _loadGate.WaitAsync(0).ConfigureAwait(true)) return;
        try
        {
            IsBusy = true;
            StatusText = "Checking downloads stored on this PC…";
            var records = await _downloads.GetDownloadsAsync().ConfigureAwait(true);
            Rebuild(records);
            _isLoaded = true;
            StatusText = HasDownloads
                ? $"{CountText} available without streaming from the server."
                : "Download a broadcast from the Library to keep it on this PC.";
        }
        finally
        {
            IsBusy = false;
            _loadGate.Release();
        }
    }

    public async Task DownloadBroadcastAsync(long representativeEpisodeId, string? title = null)
    {
        if (representativeEpisodeId <= 0 || IsDownloading) return;
        _activeDownload?.Dispose();
        _activeDownload = new CancellationTokenSource();
        IsDownloading = true;
        ActiveTitle = string.IsNullOrWhiteSpace(title) ? "Downloading broadcast" : title.Trim();
        ActiveDetail = "Preparing the canonical recording…";
        DownloadPercent = 0;
        var progress = new Progress<NativeDownloadProgress>(value =>
        {
            ActiveTitle = value.Title;
            DownloadPercent = value.Percent;
            ActiveDetail = value.TotalBytes > 0
                ? $"Part {value.PartNumber:N0} of {value.PartCount:N0} · {FormatBytes(value.BytesReceived)} of {FormatBytes(value.TotalBytes)}"
                : $"Part {value.PartNumber:N0} of {value.PartCount:N0} · {FormatBytes(value.BytesReceived)}";
        });

        try
        {
            await _downloads.DownloadAsync(representativeEpisodeId, progress, _activeDownload.Token).ConfigureAwait(true);
            await LoadAsync(force: true).ConfigureAwait(true);
            StatusText = $"{ActiveTitle} is ready offline on this PC.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Download cancelled. No incomplete broadcast was added.";
        }
        catch (Exception exception)
        {
            SetError(exception);
        }
        finally
        {
            IsDownloading = false;
            _activeDownload.Dispose();
            _activeDownload = null;
        }
    }

    private async Task PlayAsync(NativeDownloadRowViewModel row)
    {
        if (row.NeedsRepair)
        {
            StatusText = "Repair this download before playing it.";
            return;
        }
        await _playback.LoadAndPlayAsync(row.RepresentativeEpisodeId).ConfigureAwait(true);
    }

    private Task RepairAsync(NativeDownloadRowViewModel row)
        => DownloadBroadcastAsync(row.RepresentativeEpisodeId, row.Title);

    private async Task RemoveAsync(NativeDownloadRowViewModel row)
    {
        var confirmed = await _notifications.ConfirmAsync(
            "Remove download?",
            $"Remove the offline copy of “{row.Title}” from this PC? The broadcast will remain on Radio Vault Server.").ConfigureAwait(true);
        if (!confirmed) return;
        IsBusy = true;
        try
        {
            await _downloads.RemoveAsync(row.RepresentativeEpisodeId).ConfigureAwait(true);
            await LoadAsync(force: true).ConfigureAwait(true);
            StatusText = "The offline copy was removed from this PC.";
        }
        finally { IsBusy = false; }
    }

    private async Task RemoveAllAsync()
    {
        var confirmed = await _notifications.ConfirmAsync(
            "Remove every download?",
            "Remove every offline broadcast stored by Radio Vault on this PC? The server Library will not be changed.").ConfigureAwait(true);
        if (!confirmed) return;
        IsBusy = true;
        try
        {
            await _downloads.RemoveAllAsync().ConfigureAwait(true);
            await LoadAsync(force: true).ConfigureAwait(true);
            StatusText = "All offline copies were removed from this PC.";
        }
        finally { IsBusy = false; }
    }

    private async Task AuditAsync()
    {
        IsBusy = true;
        try
        {
            StatusText = "Checking every downloaded media part…";
            var result = await _downloads.AuditAsync().ConfigureAwait(true);
            await LoadAsync(force: true).ConfigureAwait(true);
            StatusText = result.NeedsRepair == 0
                ? $"Checked {result.Checked:N0} downloads; every media part is present."
                : $"Checked {result.Checked:N0} downloads; {result.NeedsRepair:N0} need repair.";
        }
        finally { IsBusy = false; }
    }

    private void Rebuild(IReadOnlyList<NativeDownloadRecord> records)
    {
        Items.Clear();
        _downloadedIds.Clear();
        foreach (var record in records)
        {
            _downloadedIds.Add(record.RepresentativeEpisodeId);
            Items.Add(new NativeDownloadRowViewModel(record, PlayAsync, RepairAsync, RemoveAsync));
        }
        StoredBytes = records.Sum(record => Math.Max(0, record.SizeBytes));
        RepairCount = records.Count(record => record.NeedsRepair);
        RaisePropertyChanged(nameof(HasDownloads));
        RaisePropertyChanged(nameof(HasNoDownloads));
        RaisePropertyChanged(nameof(CountText));
        RaiseCommandState();
        DownloadsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DownloadsOnChanged(object? sender, EventArgs e)
    {
        if (_disposed || IsDownloading || IsBusy) return;
        _ = _dispatcher.InvokeAsync(() => _ = LoadAsync(force: true));
    }

    private void CancelDownload() => _activeDownload?.Cancel();

    private void SetError(Exception exception)
        => StatusText = $"Download failed: {exception.Message}";

    private void RaiseCommandState()
    {
        if (RefreshCommand is AsyncCommand refresh) refresh.RaiseCanExecuteChanged();
        if (AuditCommand is AsyncCommand audit) audit.RaiseCanExecuteChanged();
        if (RemoveAllCommand is AsyncCommand removeAll) removeAll.RaiseCanExecuteChanged();
        if (CancelDownloadCommand is DelegateCommand cancel) cancel.RaiseCanExecuteChanged();
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024d * 1024d * 1024d):0.##} GB";
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024d * 1024d):0.#} MB";
        if (bytes >= 1024L) return $"{bytes / 1024d:0.#} KB";
        return $"{Math.Max(0, bytes):N0} B";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _downloads.DownloadsChanged -= DownloadsOnChanged;
        _activeDownload?.Cancel();
        _activeDownload?.Dispose();
        _loadGate.Dispose();
    }
}

public sealed class NativeDownloadRowViewModel
{
    public NativeDownloadRowViewModel(
        NativeDownloadRecord record,
        Func<NativeDownloadRowViewModel, Task> play,
        Func<NativeDownloadRowViewModel, Task> repair,
        Func<NativeDownloadRowViewModel, Task> remove)
    {
        Record = record ?? throw new ArgumentNullException(nameof(record));
        PlayCommand = new AsyncCommand(() => play(this), () => !NeedsRepair);
        RepairCommand = new AsyncCommand(() => repair(this));
        RemoveCommand = new AsyncCommand(() => remove(this));
    }

    public NativeDownloadRecord Record { get; }
    public long RepresentativeEpisodeId => Record.RepresentativeEpisodeId;
    public string Title => Record.Title;
    public string CollectionName => Record.CollectionName;
    public string DateText => Record.AirDate?.ToString("ddd, d MMM yyyy") ?? "Date unknown";
    public string SizeText => DownloadsViewModelFormatBytes(Record.SizeBytes);
    public string PartsText => Record.Parts.Count == 1 ? "1 recording" : $"{Record.Parts.Count:N0} recording parts";
    public string DownloadedText => $"Downloaded {Record.DownloadedAt.ToLocalTime():d MMM yyyy}";
    public bool NeedsRepair => Record.NeedsRepair;
    public string RepairText => NeedsRepair ? Record.RepairState : "Available offline";
    public string? ArtworkPath => Record.ArtworkPath;
    public bool HasArtwork => !string.IsNullOrWhiteSpace(ArtworkPath) && File.Exists(ArtworkPath);
    public ICommand PlayCommand { get; }
    public ICommand RepairCommand { get; }
    public ICommand RemoveCommand { get; }

    private static string DownloadsViewModelFormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024d * 1024d * 1024d):0.##} GB";
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024d * 1024d):0.#} MB";
        if (bytes >= 1024L) return $"{bytes / 1024d:0.#} KB";
        return $"{Math.Max(0, bytes):N0} B";
    }
}
