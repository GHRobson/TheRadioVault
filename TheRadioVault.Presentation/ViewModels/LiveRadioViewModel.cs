using System.Collections.ObjectModel;
using System.Windows.Input;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;
using TheRadioVault.Application.Services;
using TheRadioVault.Core.Playback;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

/// <summary>
/// A deliberately isolated player for the clock-driven archive station. It
/// resolves media for reading but never calls the Library progress writer or
/// participates in the shared playback/handoff session.
/// </summary>
public sealed class LiveRadioViewModel : ObservableObject, IDisposable
{
    private readonly ILiveRadioService _radio;
    private readonly ILocalPlaybackLibraryService _library;
    private readonly IMomentsService _moments;
    private readonly PlaybackSessionCoordinator _session;
    private readonly PlaybackViewModel _ordinaryPlayback;
    private readonly IUiDispatcher _dispatcher;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly Timer _timer;
    private LiveRadioSnapshot? _snapshot;
    private LiveRadioProgramme? _programme;
    private LocalPlaybackDescriptor? _descriptor;
    private LocalPlaybackSegment? _segment;
    private int _segmentIndex;
    private DateTimeOffset _snapshotReceivedAt;
    private DateTimeOffset _lastServerRefresh = DateTimeOffset.MinValue;
    private bool _isBusy;
    private bool _isTunedIn;
    private bool _isPlaying;
    private string _stationName = "Radio Vault Live";
    private string _title = "Your archive, broadcasting now";
    private string _subtitle = "Tune in to hear what Radio Vault has scheduled.";
    private string _selectionReason = string.Empty;
    private string _statusText = "Ready";
    private long _positionMs;
    private long _durationMs;
    private bool _disposed;

    public LiveRadioViewModel(
        ILiveRadioService radio,
        ILocalPlaybackLibraryService library,
        IMomentsService moments,
        PlaybackSessionCoordinator session,
        PlaybackViewModel ordinaryPlayback,
        IUiDispatcher dispatcher)
    {
        _radio = radio ?? throw new ArgumentNullException(nameof(radio));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _moments = moments ?? throw new ArgumentNullException(nameof(moments));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _ordinaryPlayback = ordinaryPlayback ?? throw new ArgumentNullException(nameof(ordinaryPlayback));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        RefreshCommand = new AsyncCommand(() => LoadAsync(force: true), () => !IsBusy, SetError);
        TuneInCommand = new AsyncCommand(TuneInAsync, () => !IsBusy && CurrentProgrammeAvailable, SetError);
        LeaveCommand = new DelegateCommand(Leave, () => IsTunedIn);
        SaveMomentCommand = new AsyncCommand(SaveMomentAsync, () => IsTunedIn && !IsBusy, SetError);
        _session.StateChanged += SessionOnStateChanged;
        _session.MediaEnded += SessionOnMediaEnded;
        _session.MediaFailed += SessionOnMediaFailed;
        _timer = new Timer(_ => _ = SynchronizeAsync(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public ICommand RefreshCommand { get; }
    public ICommand TuneInCommand { get; }
    public ICommand LeaveCommand { get; }
    public ICommand SaveMomentCommand { get; }
    public ObservableCollection<LiveRadioProgrammeRowViewModel> Upcoming { get; } = new();
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandState(); } }
    public bool IsTunedIn { get => _isTunedIn; private set { if (SetProperty(ref _isTunedIn, value)) RaiseCommandState(); } }
    public bool IsPlaying { get => _isPlaying; private set => SetProperty(ref _isPlaying, value); }
    public bool CurrentProgrammeAvailable => _snapshot?.Current is not null;
    public string StationName { get => _stationName; private set => SetProperty(ref _stationName, value); }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => SetProperty(ref _subtitle, value); }
    public string SelectionReason { get => _selectionReason; private set => SetProperty(ref _selectionReason, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public long PositionMs { get => _positionMs; private set { if (SetProperty(ref _positionMs, Math.Max(0, value))) RaiseTimeline(); } }
    public long DurationMs { get => _durationMs; private set { if (SetProperty(ref _durationMs, Math.Max(0, value))) RaiseTimeline(); } }
    public double ProgressPercent => DurationMs <= 0 ? 0 : Math.Clamp(PositionMs * 100d / DurationMs, 0, 100);
    public string PositionText => FormatTime(PositionMs);
    public string DurationText => FormatTime(DurationMs);
    public string RemainingText => "-" + FormatTime(Math.Max(0, DurationMs - PositionMs));
    public string TuneButtonText => IsTunedIn ? "On air" : "Tune in";

    public async Task LoadAsync(bool force = false)
    {
        if (_disposed || IsBusy || !force && _snapshot is not null && DateTimeOffset.UtcNow - _lastServerRefresh < TimeSpan.FromSeconds(30)) return;
        IsBusy = true;
        try
        {
            var snapshot = await _radio.GetSnapshotAsync().ConfigureAwait(true);
            AdoptSnapshot(snapshot);
            StatusText = snapshot.Current is null
                ? "The station is preparing its schedule."
                : IsTunedIn ? "Live on this computer" : "Ready to tune in";
        }
        finally { IsBusy = false; }
    }

    private async Task TuneInAsync()
    {
        if (_snapshot?.Current is null)
            await LoadAsync(force: true).ConfigureAwait(true);
        var programme = _snapshot?.Current ?? throw new InvalidOperationException("Radio Vault Live does not have a programme on air yet.");
        IsBusy = true;
        StatusText = "Tuning in…";
        try
        {
            if (_ordinaryPlayback.IsPlaying && !_ordinaryPlayback.IsPlaybackElsewhere)
                _ordinaryPlayback.Stop();
            await OpenProgrammeAsync(programme).ConfigureAwait(true);
            IsTunedIn = true;
            StatusText = "Live on this computer";
        }
        finally { IsBusy = false; }
    }

    public void Leave()
    {
        if (!IsTunedIn) return;
        _session.Stop();
        IsTunedIn = false;
        IsPlaying = false;
        StatusText = "Left Radio Vault Live";
        RaisePropertyChanged(nameof(TuneButtonText));
    }

    private async Task SaveMomentAsync()
    {
        var programme = _programme ?? throw new InvalidOperationException("Tune in before saving a live Moment.");
        var position = CapturePosition();
        IsBusy = true;
        try
        {
            await _moments.AddAsync(
                programme.Broadcast.RepresentativeEpisodeId,
                position,
                $"Live radio at {FormatTime(position)}",
                "Saved from Radio Vault Live").ConfigureAwait(true);
            StatusText = "Moment saved. It will open at this exact point in the regular Library.";
        }
        finally { IsBusy = false; }
    }

    private async Task OpenProgrammeAsync(LiveRadioProgramme programme)
    {
        var descriptor = await _library.PrepareAsync(
            programme.Broadcast.CanonicalKey,
            programme.Broadcast.RepresentativeEpisodeId).ConfigureAwait(true);
        if (descriptor.Segments.Count == 0)
            throw new InvalidOperationException("The programme on air has no playable media.");
        _programme = programme;
        _descriptor = descriptor;
        Title = string.IsNullOrWhiteSpace(programme.Broadcast.Title)
            ? programme.Broadcast.CollectionName
            : programme.Broadcast.Title!;
        Subtitle = programme.Broadcast.AirDate is { } date
            ? $"{programme.Broadcast.CollectionName} · {date:dd MMM yyyy}"
            : programme.Broadcast.CollectionName;
        SelectionReason = programme.SelectionReason;
        DurationMs = descriptor.DurationMs;
        var position = ProjectPosition(programme);
        _session.SelectBroadcast(programme.Broadcast.RepresentativeEpisodeId);
        OpenPosition(position, autoPlay: true);
    }

    private void OpenPosition(long logicalPositionMs, bool autoPlay)
    {
        var descriptor = _descriptor ?? throw new InvalidOperationException("No live programme is prepared.");
        logicalPositionMs = Math.Clamp(logicalPositionMs, 0, Math.Max(0, descriptor.DurationMs - 1));
        var index = descriptor.Segments.ToList().FindIndex(segment =>
            logicalPositionMs >= segment.LogicalStartMs && logicalPositionMs < segment.LogicalEndMs);
        if (index < 0) index = descriptor.Segments.Count - 1;
        _segmentIndex = index;
        _segment = descriptor.Segments[index];
        _session.Open(_segment.MediaPath);
        _session.SetSpeed(1d);
        _session.Seek(TimeSpan.FromMilliseconds(Math.Max(0, logicalPositionMs - _segment.LogicalStartMs)));
        PositionMs = logicalPositionMs;
        if (autoPlay) _session.Play();
    }

    private async Task SynchronizeAsync()
    {
        if (_disposed || !await _syncGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            if (IsTunedIn)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    PositionMs = CapturePosition();
                    IsPlaying = _session.IsPlaying;
                }).ConfigureAwait(false);
            }
            var interval = IsTunedIn ? TimeSpan.FromSeconds(15) : TimeSpan.FromMinutes(1);
            if (DateTimeOffset.UtcNow - _lastServerRefresh < interval) return;
            var snapshot = await _radio.GetSnapshotAsync().ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => AdoptSnapshot(snapshot)).ConfigureAwait(false);
            if (!IsTunedIn || snapshot.Current is null) return;
            if (_programme?.ScheduleEntryId != snapshot.Current.ScheduleEntryId)
            {
                await _dispatcher.InvokeAsync(() => StatusText = "Tuning to the next live programme…").ConfigureAwait(false);
                Task? openTask = null;
                await _dispatcher.InvokeAsync(() => openTask = OpenProgrammeAsync(snapshot.Current)).ConfigureAwait(false);
                if (openTask is not null) await openTask.ConfigureAwait(false);
                return;
            }
            var target = ProjectPosition(snapshot.Current);
            var actual = await _dispatcher.InvokeAsync(CapturePosition).ConfigureAwait(false);
            if (Math.Abs(target - actual) > 5_000)
                await _dispatcher.InvokeAsync(() => OpenPosition(target, autoPlay: true)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _dispatcher.InvokeAsync(() => StatusText = "Live Radio connection: " + exception.Message).ConfigureAwait(false);
        }
        finally { _syncGate.Release(); }
    }

    private void AdoptSnapshot(LiveRadioSnapshot snapshot)
    {
        _snapshot = snapshot;
        _snapshotReceivedAt = DateTimeOffset.UtcNow;
        _lastServerRefresh = _snapshotReceivedAt;
        StationName = snapshot.StationName;
        if (!IsTunedIn && snapshot.Current is { } current)
        {
            Title = string.IsNullOrWhiteSpace(current.Broadcast.Title) ? current.Broadcast.CollectionName : current.Broadcast.Title!;
            Subtitle = current.Broadcast.AirDate is { } date
                ? $"{current.Broadcast.CollectionName} · {date:dd MMM yyyy}"
                : current.Broadcast.CollectionName;
            SelectionReason = current.SelectionReason;
            PositionMs = current.PositionMs;
            DurationMs = current.Broadcast.DurationMs;
        }
        Upcoming.Clear();
        foreach (var item in snapshot.Upcoming) Upcoming.Add(new LiveRadioProgrammeRowViewModel(item));
        RaisePropertyChanged(nameof(CurrentProgrammeAvailable));
        RaiseCommandState();
    }

    private long ProjectPosition(LiveRadioProgramme programme)
    {
        var basePosition = _snapshot?.Current?.ScheduleEntryId == programme.ScheduleEntryId
            ? programme.PositionMs + (long)(DateTimeOffset.UtcNow - _snapshotReceivedAt).TotalMilliseconds
            : programme.PositionMs;
        return Math.Clamp(basePosition, 0, Math.Max(0, programme.Broadcast.DurationMs - 1));
    }

    private long CapturePosition()
        => _segment is null ? PositionMs : Math.Clamp(
            _segment.LogicalStartMs + (long)_session.Position.TotalMilliseconds,
            0,
            Math.Max(0, DurationMs));

    private void SessionOnStateChanged(object? sender, PlaybackSessionSnapshot snapshot)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            if (!IsTunedIn) return;
            PositionMs = CapturePosition();
            IsPlaying = snapshot.IsPlaying;
        });
    }

    private void SessionOnMediaEnded(object? sender, EventArgs eventArgs)
        => _ = HandleMediaEndedAsync();

    private async Task HandleMediaEndedAsync()
    {
        var synchronize = false;
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!IsTunedIn || _descriptor is null) return;
                if (_segmentIndex + 1 < _descriptor.Segments.Count)
                {
                    OpenPosition(_descriptor.Segments[_segmentIndex + 1].LogicalStartMs, autoPlay: true);
                    return;
                }
                _lastServerRefresh = DateTimeOffset.MinValue;
                synchronize = true;
            }).ConfigureAwait(false);
            if (synchronize) await SynchronizeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _dispatcher.InvokeAsync(() => StatusText = "The next live programme could not be opened: " + exception.Message)
                .ConfigureAwait(false);
        }
    }

    private void SessionOnMediaFailed(object? sender, PlaybackErrorEventArgs eventArgs)
        => _ = _dispatcher.InvokeAsync(() =>
            StatusText = "Live Radio playback failed: " + eventArgs.ErrorException.Message);

    private void SetError(Exception exception) => StatusText = exception.Message;

    private void RaiseTimeline()
    {
        RaisePropertyChanged(nameof(ProgressPercent));
        RaisePropertyChanged(nameof(PositionText));
        RaisePropertyChanged(nameof(DurationText));
        RaisePropertyChanged(nameof(RemainingText));
    }

    private void RaiseCommandState()
    {
        RaisePropertyChanged(nameof(TuneButtonText));
        ((AsyncCommand)RefreshCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)TuneInCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)LeaveCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)SaveMomentCommand).RaiseCanExecuteChanged();
    }

    private static string FormatTime(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Dispose();
        _session.StateChanged -= SessionOnStateChanged;
        _session.MediaEnded -= SessionOnMediaEnded;
        _session.MediaFailed -= SessionOnMediaFailed;
        _session.Dispose();
        _syncGate.Dispose();
    }
}

public sealed class LiveRadioProgrammeRowViewModel
{
    public LiveRadioProgrammeRowViewModel(LiveRadioProgramme value)
    {
        Value = value;
        Title = string.IsNullOrWhiteSpace(value.Broadcast.Title) ? value.Broadcast.CollectionName : value.Broadcast.Title!;
        Detail = $"{value.StartsAt.ToLocalTime():HH:mm} · {value.Broadcast.CollectionName}";
    }

    public LiveRadioProgramme Value { get; }
    public string Title { get; }
    public string Detail { get; }
    public string Reason => Value.SelectionReason;
}
