using System.Collections.ObjectModel;
using System.Windows.Input;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;
using TheRadioVault.Application.Services;
using TheRadioVault.Core.Playback;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Diagnostics;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class PlaybackViewModel : ObservableObject, IDisposable
{
    private static readonly double[] Speeds = { 0.5d, 0.75d, 1d, 1.25d, 1.5d, 1.75d, 2d, 2.5d, 3d };
    // Keep this in step with PlaybackTransferCoordinator.CommitToleranceMs.
    // A playing source advances while the target decoder opens; using a tighter
    // client-only window makes the target chase a moving playhead forever even
    // though the server would safely accept the prepared decoder.
    private const long PlaybackTransferAlignmentToleranceMs = 3_000;
    private static readonly TimeSpan PlaybackTransferDecoderReadyTimeout = TimeSpan.FromSeconds(10);
    private readonly PlaybackSessionCoordinator _session;
    private readonly ILocalPlaybackLibraryService _library;
    private readonly ILibraryActionService _actions;
    private readonly IQueueService _queue;
    private readonly IUiDispatcher _dispatcher;
    private readonly IPlaybackHandoffService _handoff;
    private readonly string _playbackSourceText;
    private readonly PlaybackProgressCoordinator _progress = new();
    private readonly PlaybackCompletionCoordinator _completion = new();
    private readonly SemaphoreSlim _saveGate = new(1, 1);
    private readonly Timer _saveTimer;
    private readonly Timer _handoffTimer;
    private readonly Timer _handoffReportTimer;
    private readonly Timer _remoteProjectionTimer;
    private readonly Timer _loadingGlyphTimer;
    private readonly SemaphoreSlim _handoffReportGate = new(1, 1);
    private readonly SemaphoreSlim _handoffRefreshGate = new(1, 1);
    private LocalPlaybackDescriptor? _descriptor;
    private LocalPlaybackSegment? _segment;
    private int _segmentIndex;
    private long _logicalPositionMs;
    private long _latestObservedPositionMs;
    private long _logicalDurationMs;
    private bool _isLoaded;
    private bool _isPlaying;
    private bool _isBusy;
    private bool _isSeeking;
    private bool _playCountPending;
    private bool _completed;
    private bool _completionResetPending;
    private long _playbackGeneration;
    private CancellationTokenSource? _seekCancellation;
    private bool _isFavourite;
    private string _title = "Nothing playing";
    private string _subtitle = "Choose a broadcast from the Library";
    private string _statusText = "Ready";
    private string? _artworkPath;
    private double _volume = 0.8d;
    private double _speed = 1d;
    private int _skipBackSeconds = 15;
    private int _skipForwardSeconds = 30;
    private int _completionThresholdSeconds = 30;
    private bool _isPlaybackElsewhere;
    private string _activeDeviceText = "Playback on this device";
    private string _activeDeviceDetail = string.Empty;
    private PlaybackHandoffSnapshot? _handoffSnapshot;
    private PlaybackLiveProgress? _liveProgress;
    private bool _transportPending;
    private bool _desiredPlaying;
    private bool _transportIntentChanged;
    private int _primaryTransportActionActive;
    private int _sourceStopAcknowledgementActive;
    private int _loadingGlyphFrame;
    private int _shutdownPersistenceActive;
    private int _shutdownPersistenceCompleted;
    private long _lastResolveDurationMs;
    private long _lastDecoderOpenDurationMs;
    private long _lastStartupDurationMs;
    private long? _remoteProjectionEpisodeId;
    private long _remoteProjectionGeneration = -1;
    private string _remoteProjectionOwnerId = string.Empty;
    private long _remoteProjectionPositionMs;
    private bool _disposed;

    public PlaybackViewModel(
        PlaybackSessionCoordinator session,
        ILocalPlaybackLibraryService library,
        ILibraryActionService actions,
        IQueueService queue,
        IUiDispatcher dispatcher,
        IPlaybackHandoffService handoff,
        string playbackSourceText = "local archive")
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _handoff = handoff ?? throw new ArgumentNullException(nameof(handoff));
        _playbackSourceText = string.IsNullOrWhiteSpace(playbackSourceText) ? "archive" : playbackSourceText.Trim();
        PlayPauseCommand = new DelegateCommand(ExecutePrimaryTransport, CanExecutePrimaryTransport);
        SkipBackCommand = new DelegateCommand(() => Skip(TimeSpan.FromSeconds(-_skipBackSeconds)), () => CanControlLocalTransport);
        SkipForwardCommand = new DelegateCommand(() => Skip(TimeSpan.FromSeconds(_skipForwardSeconds)), () => CanControlLocalTransport);
        StopCommand = new DelegateCommand(Stop, () => CanControlLocalTransport);
        CycleSpeedCommand = new DelegateCommand(CycleSpeed, () => CanControlLocalTransport);
        ToggleFavouriteCommand = new AsyncCommand(ToggleFavouriteAsync, () => IsLoaded && !IsBusy, SetCommandError);
        AddToQueueCommand = new AsyncCommand(() => QueueCurrentAsync(false), () => IsLoaded && !IsBusy, SetCommandError);
        PlayNextCommand = new AsyncCommand(() => QueueCurrentAsync(true), () => IsLoaded && !IsBusy, SetCommandError);
        ContinueOnThisDeviceCommand = new AsyncCommand(ContinueOnThisDeviceAsync, () => IsPlaybackElsewhere && !IsBusy, SetCommandError);
        MoveToServerCommand = new AsyncCommand(MoveToServerAsync, () => CanMovePlaybackToServer && HasCurrentBroadcast && !IsBusy, SetCommandError);
        _session.StateChanged += SessionOnStateChanged;
        _session.MediaEnded += SessionOnMediaEnded;
        _session.MediaFailed += SessionOnMediaFailed;
        _saveTimer = new Timer(_ => _ = SaveProgressAsync(false), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
        var handoffDue = _handoff.IsAvailable ? TimeSpan.FromMilliseconds(350) : Timeout.InfiniteTimeSpan;
        var handoffReportDue = _handoff.IsAvailable ? TimeSpan.FromMilliseconds(500) : Timeout.InfiniteTimeSpan;
        var projectionDue = _handoff.IsAvailable ? TimeSpan.FromMilliseconds(250) : Timeout.InfiniteTimeSpan;
        _handoffTimer = new Timer(_ => _ = RefreshHandoffAsync(), null, handoffDue, TimeSpan.FromSeconds(1));
        _handoffReportTimer = new Timer(_ => _ = ReportHandoffProgressAsync(), null, handoffReportDue, TimeSpan.FromSeconds(1));
        _remoteProjectionTimer = new Timer(_ => _ = UpdateProjectedRemoteStateAsync(), null, projectionDue, TimeSpan.FromMilliseconds(250));
        _loadingGlyphTimer = new Timer(_ => _ = AdvanceLoadingGlyphAsync(), null, TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(60));
    }

    public event EventHandler<PlaybackFavouriteChangedEventArgs>? FavouriteChanged;
    public event EventHandler? QueueChanged;

    public ICommand PlayPauseCommand { get; }
    public ICommand SkipBackCommand { get; }
    public ICommand SkipForwardCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand CycleSpeedCommand { get; }
    public ICommand ToggleFavouriteCommand { get; }
    public ICommand AddToQueueCommand { get; }
    public ICommand PlayNextCommand { get; }
    public ICommand ContinueOnThisDeviceCommand { get; }
    public ICommand MoveToServerCommand { get; }
    public ObservableCollection<PlaybackDeviceState> HandoffDevices { get; } = new();

    public bool IsHandoffAvailable => _handoff.IsAvailable;
    public bool HasHandoffDevices => HandoffDevices.Count > 0;
    public bool CanMovePlaybackToServer => false;

    public bool IsLoaded
    {
        get => _isLoaded;
        private set
        {
            if (!SetProperty(ref _isLoaded, value)) return;
            RaiseCommandState();
            PublishLiveProgress();
        }
    }
    public bool IsPlaying
    {
        get => _isPlaying;
        private set
        {
            if (!SetProperty(ref _isPlaying, value)) return;
            if (!_transportPending) _desiredPlaying = value;
            RaisePrimaryTransportState();
            PublishLiveProgress();
        }
    }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RaiseCommandState();
            RaisePrimaryTransportState();
        }
    }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Subtitle { get => _subtitle; private set => SetProperty(ref _subtitle, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public long LastResolveDurationMs { get => _lastResolveDurationMs; private set => SetProperty(ref _lastResolveDurationMs, Math.Max(0, value)); }
    public long LastDecoderOpenDurationMs { get => _lastDecoderOpenDurationMs; private set => SetProperty(ref _lastDecoderOpenDurationMs, Math.Max(0, value)); }
    public long LastStartupDurationMs { get => _lastStartupDurationMs; private set => SetProperty(ref _lastStartupDurationMs, Math.Max(0, value)); }
    public string LastStartupTimingText => LastStartupDurationMs <= 0
        ? string.Empty
        : $"Started in {LastStartupDurationMs / 1000d:0.0}s · lookup {LastResolveDurationMs / 1000d:0.0}s · audio {LastDecoderOpenDurationMs / 1000d:0.0}s";
    public bool HasStartupTiming => LastStartupDurationMs > 0;
    public string? ArtworkPath { get => _artworkPath; private set { if (SetProperty(ref _artworkPath, value)) RaisePropertyChanged(nameof(HasArtwork)); } }
    public bool HasArtwork => !string.IsNullOrWhiteSpace(ArtworkPath) && File.Exists(ArtworkPath);
    public bool IsPrimaryTransportLoading => _transportPending || Volatile.Read(ref _primaryTransportActionActive) != 0;
    public double PrimaryTransportSpinnerAngle => (_loadingGlyphFrame % 12) * 30d;
    public bool ShowMoveToThisDevice => _handoff.IsAvailable && IsPlaybackElsewhere && !IsPrimaryTransportLoading;
    public bool ShowLocalPlayPause => !IsPrimaryTransportLoading && !ShowMoveToThisDevice;
    public bool ShowPlayIcon => ShowLocalPlayPause && !(_transportPending ? _desiredPlaying : IsPlaying);
    public bool ShowPauseIcon => ShowLocalPlayPause && (_transportPending ? _desiredPlaying : IsPlaying);
    public bool CanControlLocalTransport => IsLoaded && !IsBusy && !IsPlaybackElsewhere;
    public string PlayPauseGlyph => (_transportPending ? _desiredPlaying : IsPlaying) ? "Ⅱ" : "▶";
    public string PrimaryTransportToolTip => IsPrimaryTransportLoading
        ? IsPlaybackElsewhere ? "Moving playback to this device…" : "Preparing playback…"
        : ShowMoveToThisDevice
            ? "Move playback to this device"
            : IsPlaying ? "Pause" : "Play";
    public long CurrentBroadcastId => _descriptor?.RepresentativeEpisodeId ?? 0;
    public string CurrentCanonicalKey => _descriptor?.CanonicalKey ?? string.Empty;
    public bool HasCurrentBroadcast => CurrentBroadcastId > 0;
    public bool IsFavourite
    {
        get => _isFavourite;
        private set
        {
            if (!SetProperty(ref _isFavourite, value)) return;
            RaisePropertyChanged(nameof(IsNotFavourite));
            RaisePropertyChanged(nameof(FavouriteGlyph));
            RaisePropertyChanged(nameof(FavouriteToolTip));
        }
    }
    public bool IsNotFavourite => !IsFavourite;
    public string FavouriteGlyph => IsFavourite ? "♥" : "♡";
    public string FavouriteToolTip => IsFavourite ? "Remove from favourites" : "Add to favourites";
    public bool IsPlaybackElsewhere
    {
        get => _isPlaybackElsewhere;
        private set
        {
            if (!SetProperty(ref _isPlaybackElsewhere, value)) return;
            RaisePropertyChanged(nameof(ShowContinueOnThisDevice));
            RaisePrimaryTransportState();
            RaiseCommandState();
            ((AsyncCommand)ContinueOnThisDeviceCommand).RaiseCanExecuteChanged();
            PublishLiveProgress();
        }
    }
    // Kept for command/API compatibility; the visible handoff action now lives in
    // the contextual centre transport button, matching the accepted WPF design.
    public bool ShowContinueOnThisDevice => ShowMoveToThisDevice;
    public string ActiveDeviceText { get => _activeDeviceText; private set => SetProperty(ref _activeDeviceText, value); }
    public string ActiveDeviceDetail { get => _activeDeviceDetail; private set => SetProperty(ref _activeDeviceDetail, value); }
    public PlaybackLiveProgress? LiveProgress
    {
        get => _liveProgress;
        private set => SetProperty(ref _liveProgress, value);
    }

    public long PositionMs
    {
        get => _logicalPositionMs;
        private set
        {
            var normalized = Math.Max(0, value);
            Interlocked.Exchange(ref _latestObservedPositionMs, normalized);
            if (SetProperty(ref _logicalPositionMs, normalized))
            {
                RaisePropertyChanged(nameof(PositionText));
                RaisePropertyChanged(nameof(ProgressPercent));
                PublishLiveProgress();
            }
        }
    }
    public long DurationMs
    {
        get => _logicalDurationMs;
        private set
        {
            if (SetProperty(ref _logicalDurationMs, Math.Max(0, value)))
            {
                RaisePropertyChanged(nameof(DurationText));
                RaisePropertyChanged(nameof(ProgressPercent));
                PublishLiveProgress();
            }
        }
    }
    public double ProgressPercent => _completed
        ? 100d
        : DurationMs <= 0
            ? 0d
            : Math.Clamp(PositionMs * 100d / DurationMs, 0d, 100d);
    public string PositionText => FormatTime(PositionMs);
    public string DurationText => FormatTime(DurationMs);
    public string SpeedText => $"{Speed:0.##}×";
    public string SegmentText => _descriptor is null || !_descriptor.IsMultipart || _segment is null
        ? string.Empty
        : $"Part {_segment.SegmentNumber} of {_segment.SegmentTotal ?? _descriptor.Segments.Count}";
    public bool IsMultipart => _descriptor?.IsMultipart == true;

    public double Volume
    {
        get => _volume;
        set
        {
            var volume = Math.Clamp(value, 0d, 1d);
            if (!SetProperty(ref _volume, volume)) return;
            if (IsLoaded) _session.SetVolume(volume);
        }
    }

    public double Speed
    {
        get => _speed;
        private set
        {
            var speed = Math.Clamp(value, 0.5d, 3d);
            if (!SetProperty(ref _speed, speed)) return;
            RaisePropertyChanged(nameof(SpeedText));
            if (IsLoaded) _session.SetSpeed(speed);
        }
    }

    public Task LoadAndPlayAsync(BroadcastRowViewModel broadcast)
        => LoadAndPlayAsync(broadcast, CancellationToken.None);

    public Task LoadAndPlayAsync(BroadcastRowViewModel broadcast, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(broadcast);
        return LoadAndPlayCoreAsync(
            () => _library.PrepareAsync(
                broadcast.Source.CanonicalKey,
                broadcast.Source.RepresentativeEpisodeId,
                cancellationToken),
            logicalPositionMs: null,
            cancellationToken);
    }

    public Task LoadAndPlayAsync(long representativeEpisodeId)
        => LoadAndPlayAsync(representativeEpisodeId, CancellationToken.None);

    public Task LoadAndPlayAsync(long representativeEpisodeId, CancellationToken cancellationToken)
        => LoadAndPlayCoreAsync(
            () => _library.PrepareAsync(representativeEpisodeId, cancellationToken),
            logicalPositionMs: null,
            cancellationToken);

    public Task LoadAndPlayAtAsync(long representativeEpisodeId, long logicalPositionMs, CancellationToken cancellationToken = default)
        => LoadAndPlayCoreAsync(
            () => _library.PrepareAsync(representativeEpisodeId, cancellationToken),
            Math.Max(0, logicalPositionMs),
            cancellationToken);

    private Task LoadAndPlayAtStateAsync(
        long representativeEpisodeId,
        long logicalPositionMs,
        bool play,
        bool forceTransactionalTransfer = false,
        bool skipOutgoingSave = false,
        CancellationToken cancellationToken = default)
        => LoadAndPlayCoreAsync(
            () => _library.PrepareAsync(representativeEpisodeId, cancellationToken),
            Math.Max(0, logicalPositionMs),
            cancellationToken,
            play,
            refreshSharedStateBeforeClaim: true,
            forceTransactionalTransfer: forceTransactionalTransfer,
            skipOutgoingSave: skipOutgoingSave);

    private async Task LoadAndPlayCoreAsync(
        Func<Task<LocalPlaybackDescriptor>> prepare,
        long? logicalPositionMs,
        CancellationToken cancellationToken,
        bool initialPlay = true,
        bool refreshSharedStateBeforeClaim = false,
        bool forceTransactionalTransfer = false,
        bool skipOutgoingSave = false)
    {
        if (!_dispatcher.CheckAccess())
        {
            Task? uiTask = null;
            await _dispatcher.InvokeAsync(() => uiTask = LoadAndPlayCoreAsync(prepare, logicalPositionMs, cancellationToken, initialPlay, refreshSharedStateBeforeClaim, forceTransactionalTransfer, skipOutgoingSave), cancellationToken)
                .ConfigureAwait(false);
            if (uiTask is not null) await uiTask.ConfigureAwait(false);
            return;
        }
        if (IsBusy) return;
        // Any explicit Play gesture made while another output owns the session is
        // a handoff, even when it came from a Library row instead of the centre
        // move-to-device button. The one-second ownership snapshot makes this fast;
        // the server generation check closes the race if ownership changes again.
        forceTransactionalTransfer = forceTransactionalTransfer ||
            (_handoff.IsAvailable &&
             _handoffSnapshot?.HasActivePlayback == true &&
             _handoffSnapshot.IsOwnedByCurrentDevice == false);
        _transportPending = true;
        _desiredPlaying = initialPlay;
        _transportIntentChanged = false;
        RaisePrimaryTransportState();
        IsBusy = true;
        var replacingPlayback = false;
        PlaybackTransferPlan? transfer = null;
        var transferCommitted = false;
        var transferPrimedMuted = false;
        var targetAudibleVolume = Volume;
        var playbackStartWatch = System.Diagnostics.Stopwatch.StartNew();
        var handoffStage = "resolve-broadcast";
        string? postCommitWarning = null;
        StatusText = "Resolving the preferred local recording…";
        // Yield one dispatcher turn so the contextual activity glyph is painted
        // before local inspection, HTTPS resolution or decoder opening begins.
        await Task.Yield();
        try
        {
            // A broadcast switch is a persistence boundary. Wait for any timer write
            // and commit the outgoing engine position before resolving the next item.
            if (!skipOutgoingSave)
                await SaveProgressAsync(false, cancellationToken, waitForGate: true).ConfigureAwait(true);
            var resolveWatch = System.Diagnostics.Stopwatch.StartNew();
            var descriptor = await prepare().ConfigureAwait(true);
            resolveWatch.Stop();
            LastResolveDurationMs = resolveWatch.ElapsedMilliseconds;
            RuntimeDiagnosticRecorder.Record(
                "playback", "resolve-broadcast", "passed", resolveWatch.ElapsedMilliseconds,
                $"Resolved {descriptor.Segments.Count} segment(s) from {_playbackSourceText}.",
                new Dictionary<string, string>
                {
                    ["episodeId"] = descriptor.RepresentativeEpisodeId.ToString(),
                    ["multipart"] = descriptor.IsMultipart.ToString(),
                    ["source"] = _playbackSourceText,
                    ["transactionalMove"] = forceTransactionalTransfer.ToString()
                });
            double? sharedSpeed = null;
            PlaybackHandoffSnapshot? latestSnapshot = null;
            if (_handoff.IsAvailable && forceTransactionalTransfer)
            {
                handoffStage = "read-shared-state";
                latestSnapshot = await _handoff.GetSnapshotAsync(cancellationToken).ConfigureAwait(true);
                var latest = latestSnapshot?.ActivePlayback;
                if (refreshSharedStateBeforeClaim && latest?.RepresentativeEpisodeId is > 0 &&
                    latest.RepresentativeEpisodeId.Value != descriptor.RepresentativeEpisodeId &&
                    forceTransactionalTransfer)
                {
                    throw new InvalidOperationException(
                        "Shared playback changed while this device was preparing the audio. Use Move to this device again.");
                }

                if (latest?.RepresentativeEpisodeId == descriptor.RepresentativeEpisodeId)
                {
                    // The shared playhead and speed remain authoritative, but an
                    // explicit local Play gesture must never inherit an older paused
                    // presentation snapshot. Only an actual cross-device move preserves
                    // the source device's play/pause state.
                    logicalPositionMs = latest.ProjectedPositionMs(DateTimeOffset.UtcNow);
                    sharedSpeed = latest.Speed;
                    if (forceTransactionalTransfer && !_transportIntentChanged)
                        _desiredPlaying = latest.IsPlaying;
                }
                _handoffSnapshot = latestSnapshot;

                // Starting a fresh session, or continuing on the device that already
                // owns it, is ordinary playback. Do not wrap that path in a transfer
                // ticket: doing so needlessly mutes the decoder and makes a simple Play
                // depend on handoff connectivity.
                var needsTransfer = latestSnapshot?.HasActivePlayback == true &&
                    latestSnapshot.IsOwnedByCurrentDevice == false;
                if (needsTransfer)
                {
                    handoffStage = "begin-transfer";
                    StatusText = $"Preparing playback while {latestSnapshot?.OwnerDeviceName ?? "the current device"} keeps playing…";
                    transfer = await _handoff.BeginTransferAsync(
                        descriptor.RepresentativeEpisodeId,
                        logicalPositionMs ?? descriptor.ResumePositionMs,
                        descriptor.DurationMs,
                        sharedSpeed ?? descriptor.PlaybackSpeed,
                        _desiredPlaying,
                        cancellationToken).ConfigureAwait(true);
                    logicalPositionMs = transfer.ProtectedPositionMs;
                    sharedSpeed = transfer.Speed;
                    _desiredPlaying = transfer.DesiredPlaying;
                }
            }

            replacingPlayback = true;
            var generation = Interlocked.Increment(ref _playbackGeneration);
            CancelActiveSeek();
            if (_session.IsPlaying) _session.Pause();
            IsLoaded = false;
            IsPlaying = false;
            _descriptor = descriptor;
            _playCountPending = true;
            Title = descriptor.Title;
            Subtitle = BuildSubtitle(descriptor);
            ArtworkPath = descriptor.ArtworkPath;
            DurationMs = descriptor.DurationMs;
            Speed = sharedSpeed ?? descriptor.PlaybackSpeed;
            IsFavourite = descriptor.Favourite;
            PositionMs = Math.Clamp(logicalPositionMs ?? descriptor.ResumePositionMs, 0, Math.Max(0, descriptor.DurationMs));

            // Replaying a completed broadcast creates a fresh resumable session. Keep
            // its historical completion count, but allow the active position/status to
            // return to In Progress so closing and reopening resumes the replay.
            _completionResetPending = descriptor.Completed && PositionMs + 1_000 < DurationMs;
            _completed = descriptor.Completed && !_completionResetPending;
            _completion.BeginSession(PositionMs);
            _session.SelectBroadcast(descriptor.RepresentativeEpisodeId);
            RaiseCurrentBroadcastState();
            var openedWithPlayback = _desiredPlaying;
            transferPrimedMuted = transfer is not null && openedWithPlayback;
            handoffStage = transfer is null ? "open-decoder" : "open-muted-target";
            await OpenLogicalPositionAsync(
                PositionMs,
                autoPlay: openedWithPlayback,
                cancellationToken: cancellationToken,
                expectedGeneration: generation,
                keepMutedAfterOpen: transferPrimedMuted,
                registerPlay: transfer is null).ConfigureAwait(true);
            if (_desiredPlaying && !_session.IsPlaying)
            {
                if (transferPrimedMuted) _session.SetVolume(0d);
                _session.Play();
                if (transfer is null) _ = RegisterPlayAsync();
            }
            else if (!_desiredPlaying && _session.IsPlaying)
            {
                _session.Pause();
            }
            IsLoaded = true;

            if (transfer is not null)
            {
                handoffStage = "wait-for-decoder-ready";
                StatusText = "Waiting for the prepared audio to become playable…";
                await WaitForPreparedDecoderAsync(
                    descriptor,
                    generation,
                    transfer.DesiredPlaying,
                    cancellationToken).ConfigureAwait(true);

                StatusText = "Verifying the prepared decoder…";
                const int maximumAlignmentPasses = 4;
                var aligned = false;
                for (var alignmentPass = 0; alignmentPass < maximumAlignmentPasses; alignmentPass++)
                {
                    handoffStage = $"confirm-ready-{alignmentPass + 1}";
                    var preparedPosition = CaptureCurrentLogicalPosition(descriptor);
                    transfer = await _handoff.MarkTransferReadyAsync(
                        transfer,
                        preparedPosition,
                        DurationMs,
                        decoderReady: true,
                        desiredPlaying: _desiredPlaying,
                        overrideDesiredPlaying: _transportIntentChanged,
                        speed: Speed,
                        cancellationToken: cancellationToken).ConfigureAwait(true);
                    _desiredPlaying = transfer.DesiredPlaying;
                    if (Math.Abs(Speed - transfer.Speed) >= 0.001d) Speed = transfer.Speed;
                    if (transfer.DesiredPlaying && !_session.IsPlaying)
                    {
                        _session.SetVolume(0d);
                        _session.Play();
                    }
                    else if (!transfer.DesiredPlaying && _session.IsPlaying)
                    {
                        _session.Pause();
                    }

                    // The source deliberately keeps playing while the target is
                    // prepared. Re-confirm readiness after each seek so a slow
                    // decoder open can never commit against an old source projection.
                    if (Math.Abs(preparedPosition - transfer.CommitPositionMs) <= PlaybackTransferAlignmentToleranceMs)
                    {
                        aligned = true;
                        break;
                    }

                    StatusText = "Aligning with the latest shared playhead…";
                    PositionMs = Math.Clamp(transfer.CommitPositionMs, 0, Math.Max(0, DurationMs));
                    handoffStage = $"align-live-decoder-{alignmentPass + 1}";
                    await AlignPreparedDecoderAsync(
                        descriptor,
                        PositionMs,
                        generation,
                        transfer.DesiredPlaying,
                        cancellationToken).ConfigureAwait(true);
                }

                if (!aligned)
                    throw new InvalidOperationException(
                        "The target decoder could not stay aligned with the source device. The original playback was left unchanged.");

                var finalPreparedPosition = CaptureCurrentLogicalPosition(descriptor);
                handoffStage = "commit-transfer";
                var committedSnapshot = await _handoff.CommitTransferAsync(
                    transfer,
                    finalPreparedPosition,
                    decoderRunningMuted: !transfer.DesiredPlaying || _session.IsPlaying,
                    cancellationToken: cancellationToken).ConfigureAwait(true);
                transferCommitted = true;
                _handoffSnapshot = committedSnapshot;
                IsPlaybackElsewhere = false;
                ActiveDeviceText = $"{(transfer.DesiredPlaying ? "Playing" : "Paused")} on {_handoff.CurrentDeviceName}";
                ActiveDeviceDetail = descriptor.Title;

                // Once commit succeeds, this device is the authoritative output.
                // Everything below is best-effort finalisation: a transient play-count,
                // heartbeat or cancellation failure must never pause a decoder that has
                // already taken ownership and leave the user with silence everywhere.
                try
                {
                    // Keep the new target muted until the previous physical decoder
                    // confirms that it observed the committed generation and stopped.
                    // A bounded timeout still permits takeover from a suspended/offline
                    // source, but normal connected handoffs cross a real quiescence
                    // boundary instead of relying on an assumed timer delay.
                    if (transfer.DesiredPlaying)
                    {
                        handoffStage = "wait-for-source-stop";
                        var sourceStopped = await WaitForPreviousOutputToStopAsync(
                            committedSnapshot, CancellationToken.None).ConfigureAwait(true);
                        if (!sourceStopped)
                            postCommitWarning = "Playback moved, but the previous device did not confirm that it stopped before the safety timeout.";
                    }

                    // Re-check the exact committed generation at the sound boundary,
                    // including paused-source transfers that do not need to wait for a
                    // physical stop receipt. A rapid newer handoff must keep this older
                    // target silent rather than allowing two outputs to overlap.
                    handoffStage = "verify-committed-owner";
                    await EnsureCommittedOwnershipAsync(
                        committedSnapshot.Generation, CancellationToken.None).ConfigureAwait(true);

                    handoffStage = "unmute-target";
                    await EnsureTargetOutputStateAsync(
                        transfer.DesiredPlaying,
                        targetAudibleVolume,
                        CancellationToken.None).ConfigureAwait(true);
                    transferPrimedMuted = false;
                    if (transfer.DesiredPlaying) await RegisterPlayAsync().ConfigureAwait(true);
                    await _handoff.ReportAsync(
                        descriptor.RepresentativeEpisodeId,
                        CaptureCurrentLogicalPosition(descriptor),
                        DurationMs,
                        Speed,
                        transfer.DesiredPlaying,
                        _completed,
                        cancellationToken: CancellationToken.None).ConfigureAwait(true);
                }
                catch (PlaybackOwnershipMovedException movedException)
                {
                    // A newer committed generation superseded this transfer while its
                    // target was waiting for the previous source to stop. This device
                    // must remain silent; the generic post-commit recovery path is only
                    // allowed to keep output running while this device still owns it.
                    if (_session.IsPlaying) _session.Pause();
                    _session.SetVolume(targetAudibleVolume);
                    transferPrimedMuted = false;
                    IsPlaying = false;
                    IsPlaybackElsewhere = true;
                    postCommitWarning = movedException.Message;
                }
                catch (Exception finalisationException)
                {
                    _session.SetVolume(targetAudibleVolume);
                    transferPrimedMuted = false;
                    if (transfer.DesiredPlaying && !_session.IsPlaying) _session.Play();
                    postCommitWarning =
                        $"Playback moved successfully; shared status will retry automatically. {finalisationException.Message}";
                }
            }
            else if (_handoff.IsAvailable)
            {
                // Ordinary Play is deliberately independent from handoff health. The
                // decoder is already running before ownership is published, and a
                // failed optional session update must never stop local audio or keep the
                // transport in a loading state.
                _ = PublishOrdinaryPlaybackStartAsync(
                    descriptor, CaptureCurrentLogicalPosition(descriptor), IsPlaying);
            }
            playbackStartWatch.Stop();
            LastStartupDurationMs = playbackStartWatch.ElapsedMilliseconds;
            RaisePropertyChanged(nameof(LastStartupTimingText));
            RaisePropertyChanged(nameof(HasStartupTiming));
            RuntimeDiagnosticRecorder.Record(
                "playback",
                forceTransactionalTransfer ? "move-to-device" : "ordinary-start",
                IsPlaying || !_desiredPlaying ? "passed" : "warning",
                playbackStartWatch.ElapsedMilliseconds,
                IsPlaying ? "Decoder entered Playing." : "Decoder opened in Paused state.",
                new Dictionary<string, string>
                {
                    ["episodeId"] = descriptor.RepresentativeEpisodeId.ToString(),
                    ["source"] = _playbackSourceText,
                    ["transactionalMove"] = forceTransactionalTransfer.ToString(),
                    ["positionMs"] = CaptureCurrentLogicalPosition(descriptor).ToString()
                });
            StatusText = postCommitWarning ?? (_desiredPlaying
                ? (descriptor.IsMultipart ? SegmentText : $"Playing from the {_playbackSourceText}")
                : "Paused");
            RaisePropertyChanged(nameof(IsMultipart));
            RaisePropertyChanged(nameof(SegmentText));
        }
        catch (Exception exception)
        {
            playbackStartWatch.Stop();
            RuntimeDiagnosticRecorder.Record(
                forceTransactionalTransfer ? "handoff" : "playback",
                forceTransactionalTransfer ? "move-to-device" : "ordinary-start",
                "failed",
                playbackStartWatch.ElapsedMilliseconds,
                exception.Message,
                new Dictionary<string, string>
                {
                    ["stage"] = handoffStage,
                    ["episodeId"] = (_descriptor?.RepresentativeEpisodeId ?? 0).ToString(),
                    ["transferCreated"] = (transfer is not null).ToString(),
                    ["transferCommitted"] = transferCommitted.ToString(),
                    ["exception"] = exception.GetType().Name
                });
            if (transferCommitted)
            {
                if (exception is PlaybackOwnershipMovedException)
                {
                    if (_session.IsPlaying) _session.Pause();
                    if (transferPrimedMuted) _session.SetVolume(targetAudibleVolume);
                    transferPrimedMuted = false;
                    IsLoaded = _descriptor is not null;
                    IsPlaying = false;
                    IsPlaybackElsewhere = true;
                    StatusText = exception.Message;
                }
                else
                {
                    // Commit is the irreversible ownership boundary. Never respond to a
                    // later UI/telemetry error by stopping the newly authoritative output
                    // while this generation still belongs to the current device.
                    if (transferPrimedMuted) _session.SetVolume(targetAudibleVolume);
                    transferPrimedMuted = false;
                    IsLoaded = _descriptor is not null;
                    IsPlaying = _session.IsPlaying;
                    StatusText = $"Playback moved successfully; final status will retry automatically. {exception.Message}";
                }
            }
            else
            {
                if (transfer is not null)
                {
                    try
                    {
                        await _handoff.CancelTransferAsync(transfer, exception.Message, CancellationToken.None).ConfigureAwait(true);
                    }
                    catch { }
                }
                if (replacingPlayback && _session.IsPlaying) _session.Pause();
                if (transferPrimedMuted) _session.SetVolume(targetAudibleVolume);
                StatusText = transfer is not null
                    ? $"Playback move cancelled; the original device was left unchanged. {exception.Message}"
                    : $"Playback could not start: {exception.Message}";
                IsPlaying = false;
                if (replacingPlayback) IsLoaded = false;
            }
        }
        finally
        {
            if (transferPrimedMuted) _session.SetVolume(targetAudibleVolume);
            _transportPending = false;
            _desiredPlaying = IsPlaying;
            _transportIntentChanged = false;
            IsBusy = false;
            RaisePrimaryTransportState();
        }
    }

    private async Task PublishOrdinaryPlaybackStartAsync(
        LocalPlaybackDescriptor descriptor,
        long positionMs,
        bool isPlaying)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var snapshot = await _handoff.ClaimPlaybackAsync(
                descriptor.RepresentativeEpisodeId,
                positionMs,
                descriptor.DurationMs,
                Speed,
                isPlaying,
                CancellationToken.None).ConfigureAwait(false);
            watch.Stop();
            RuntimeDiagnosticRecorder.Record(
                "handoff", "ordinary-play-claim", "passed", watch.ElapsedMilliseconds,
                snapshot is null ? "Shared playback is unavailable." : $"Ownership generation {snapshot.Generation} confirmed.",
                new Dictionary<string, string>
                {
                    ["episodeId"] = descriptor.RepresentativeEpisodeId.ToString(),
                    ["deviceId"] = _handoff.CurrentDeviceId
                });
            if (snapshot is null) return;
            await _dispatcher.InvokeAsync(() =>
            {
                _handoffSnapshot = snapshot;
                ApplyHandoffSnapshot(snapshot);
            }).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            watch.Stop();
            RuntimeDiagnosticRecorder.Record(
                "handoff", "ordinary-play-claim", "failed", watch.ElapsedMilliseconds,
                exception.Message,
                new Dictionary<string, string>
                {
                    ["episodeId"] = descriptor.RepresentativeEpisodeId.ToString(),
                    ["exception"] = exception.GetType().Name
                });
            // A network failure leaves ordinary local playback usable. A server
            // conflict is different: ownership may have moved during decoder open,
            // so reconcile once and never allow the losing decoder to stay audible.
            try
            {
                var latest = await _handoff.GetSnapshotAsync(CancellationToken.None).ConfigureAwait(false);
                if (latest?.HasActivePlayback == true && latest.IsOwnedByCurrentDevice == false)
                {
                    await _dispatcher.InvokeAsync(() =>
                    {
                        if (_session.IsPlaying) _session.Pause();
                        _handoffSnapshot = latest;
                        ApplyHandoffSnapshot(latest);
                        IsPlaying = false;
                        IsPlaybackElsewhere = true;
                        StatusText = $"Playback moved to {latest.OwnerDeviceName}.";
                    }).ConfigureAwait(false);
                    return;
                }

                if (latest is not null && !latest.HasActivePlayback)
                {
                    var retry = await _handoff.ClaimPlaybackAsync(
                        descriptor.RepresentativeEpisodeId,
                        positionMs,
                        descriptor.DurationMs,
                        Speed,
                        isPlaying,
                        CancellationToken.None).ConfigureAwait(false);
                    if (retry is not null)
                    {
                        await _dispatcher.InvokeAsync(() =>
                        {
                            _handoffSnapshot = retry;
                            ApplyHandoffSnapshot(retry);
                        }).ConfigureAwait(false);
                    }
                }
            }
            catch
            {
                // The server is still unreachable. The normal reconnect monitor
                // will reconcile this output once a live ownership snapshot returns.
            }
        }
    }

    private bool CanExecutePrimaryTransport()
    {
        if (Volatile.Read(ref _primaryTransportActionActive) != 0) return false;
        return ShowMoveToThisDevice || _transportPending || IsLoaded;
    }

    private void ExecutePrimaryTransport()
    {
        if (ShowMoveToThisDevice)
        {
            if (Interlocked.CompareExchange(ref _primaryTransportActionActive, 1, 0) != 0) return;
            RaisePrimaryTransportState();
            _ = MovePlaybackHereFromPrimaryControlAsync();
            return;
        }

        if (_transportPending)
        {
            _transportIntentChanged = true;
            _desiredPlaying = !_desiredPlaying;
            if (_desiredPlaying)
            {
                StatusText = "Playback will begin as soon as the audio is ready…";
            }
            else
            {
                if (_session.IsPlaying) _session.Pause();
                StatusText = "Playback will remain paused when the audio is ready.";
            }
            RaisePrimaryTransportState();
            return;
        }

        Toggle();
    }

    private async Task MovePlaybackHereFromPrimaryControlAsync()
    {
        try
        {
            await ContinueOnThisDeviceAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            StatusText = $"Playback could not be moved: {exception.Message}";
            await RefreshHandoffAsync().ConfigureAwait(true);
        }
        finally
        {
            Interlocked.Exchange(ref _primaryTransportActionActive, 0);
            RaisePrimaryTransportState();
        }
    }

    public bool ReleaseForRemoteHandoff(long? logicalPositionMs, string? deviceName)
    {
        var wasPlaying = _session.IsPlaying || IsPlaying;
        var previousPosition = PositionMs;
        _desiredPlaying = false;
        if (_transportPending) _transportIntentChanged = true;
        if (_session.IsPlaying) _session.Pause();
        IsPlaying = false;

        if (logicalPositionMs.HasValue && IsLoaded && !IsBusy && !IsPlaybackElsewhere)
            SeekTo(logicalPositionMs.Value);

        StatusText = $"Playback released for {NormalizeDeviceName(deviceName)}.";
        _ = SaveProgressAsync(false);
        RaisePrimaryTransportState();
        return wasPlaying || (logicalPositionMs.HasValue && Math.Abs(previousPosition - logicalPositionMs.Value) >= 250);
    }

    public void Toggle()
    {
        if (!IsLoaded) return;
        if (_session.IsPlaying)
        {
            _desiredPlaying = false;
            _session.Pause();
            _ = SaveProgressAsync(false);
        }
        else
        {
            _desiredPlaying = true;
            _session.Play();
            _ = RegisterPlayAsync();
        }
        RaisePrimaryTransportState();
    }

    public void Stop()
    {
        if (!CanControlLocalTransport) return;
        _session.Pause();
        _ = SaveProgressAsync(false);
        StatusText = "Paused";
    }

    public void SetSpeed(double speed)
    {
        if (!CanControlLocalTransport) return;
        Speed = Math.Clamp(speed, 0.5d, 3d);
        _ = SaveProgressAsync(false);
    }

    public void ApplyPlaybackPreferences(int skipBackSeconds, int skipForwardSeconds, int completionThresholdSeconds)
    {
        _skipBackSeconds = Math.Clamp(skipBackSeconds, 1, 300);
        _skipForwardSeconds = Math.Clamp(skipForwardSeconds, 1, 300);
        _completionThresholdSeconds = Math.Clamp(completionThresholdSeconds, 0, 300);
    }

    public void Skip(TimeSpan amount)
    {
        if (!CanControlLocalTransport) return;
        SeekTo(PositionMs + (long)amount.TotalMilliseconds);
    }

    public void BeginSeek()
    {
        if (!CanControlLocalTransport || _descriptor is null) return;
        // While the user owns the thumb, ignore timer-driven position snapshots.
        // The Slider's Thumb handles pointer events itself, so MainWindow subscribes
        // with handledEventsToo and this flag remains active for the full gesture.
        CancelActiveSeek();
        _isSeeking = true;
    }

    public void SeekTo(long logicalPositionMs)
    {
        if (!CanControlLocalTransport || _descriptor is null)
        {
            _isSeeking = false;
            return;
        }

        logicalPositionMs = Math.Clamp(logicalPositionMs, 0, Math.Max(0, DurationMs));
        CancelActiveSeek();
        var cancellation = new CancellationTokenSource();
        _seekCancellation = cancellation;
        _isSeeking = true;
        PositionMs = logicalPositionMs;
        if (_completed && logicalPositionMs + 1_000 < DurationMs)
        {
            _completed = false;
            _completionResetPending = true;
            PublishLiveProgress();
        }
        _completion.ResetNaturalProgress(logicalPositionMs);
        _ = SeekCoreAsync(logicalPositionMs, Volatile.Read(ref _playbackGeneration), cancellation);
    }

    public void SetFavouriteStateFromExternal(long representativeEpisodeId, bool favourite)
    {
        if (representativeEpisodeId <= 0) return;
        if (representativeEpisodeId == CurrentBroadcastId)
        {
            IsFavourite = favourite;
            if (_descriptor is not null) _descriptor = _descriptor with { Favourite = favourite };
        }
        FavouriteChanged?.Invoke(this, new PlaybackFavouriteChangedEventArgs(representativeEpisodeId, favourite));
    }

    private async Task ToggleFavouriteAsync()
    {
        var descriptor = _descriptor;
        if (descriptor is null) return;
        var next = !IsFavourite;
        await _actions.SetFavouriteAsync(descriptor.RepresentativeEpisodeId, next).ConfigureAwait(true);
        IsFavourite = next;
        _descriptor = descriptor with { Favourite = next };
        StatusText = next ? "Added to favourites" : "Removed from favourites";
        FavouriteChanged?.Invoke(this, new PlaybackFavouriteChangedEventArgs(descriptor.RepresentativeEpisodeId, next));
    }

    private async Task QueueCurrentAsync(bool playNext)
    {
        var descriptor = _descriptor;
        if (descriptor is null) return;
        await _queue.AddAsync(descriptor.RepresentativeEpisodeId, playNext).ConfigureAwait(true);
        StatusText = playNext ? "Added to play next" : "Added to queue";
        QueueChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task SeekCoreAsync(
        long logicalPositionMs,
        long generation,
        CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (generation != Volatile.Read(ref _playbackGeneration)) return;

            var target = FindSegment(logicalPositionMs);
            var shouldResume = _session.IsPlaying;
            if (_segment is not null && target.SegmentNumber == _segment.SegmentNumber)
            {
                var localTargetMs = Math.Max(0, logicalPositionMs - target.LogicalStartMs);
                _session.Seek(TimeSpan.FromMilliseconds(localTargetMs));
                await ConfirmLogicalPositionAsync(
                    _descriptor!,
                    target,
                    logicalPositionMs,
                    generation,
                    cancellation.Token).ConfigureAwait(true);
                PositionMs = logicalPositionMs;
            }
            else
            {
                await OpenLogicalPositionAsync(
                    logicalPositionMs,
                    shouldResume,
                    cancellation.Token,
                    generation).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            StatusText = exception.Message;
        }
        finally
        {
            var isCurrent = ReferenceEquals(_seekCancellation, cancellation);
            if (isCurrent)
            {
                _seekCancellation = null;
                _isSeeking = false;
            }
            cancellation.Dispose();
            if (isCurrent)
                await SaveProgressAsync(false, positionOverrideMs: logicalPositionMs, explicitSeek: true).ConfigureAwait(true);
        }
    }

    private void CancelActiveSeek()
    {
        var cancellation = _seekCancellation;
        _seekCancellation = null;
        if (cancellation is null) return;
        try { cancellation.Cancel(); }
        catch (ObjectDisposedException) { }
        // SeekCoreAsync owns disposal. Disposing here can race its cancellation
        // checks and turn an ordinary replacement seek into ObjectDisposedException.
    }

    private void CycleSpeed()
    {
        var index = Array.FindIndex(Speeds, value => value > Speed + 0.001d);
        Speed = index < 0 ? Speeds[0] : Speeds[index];
        StatusText = $"Playback speed {SpeedText}";
        _ = SaveProgressAsync(false);
    }

    private async Task OpenLogicalPositionAsync(
        long logicalPositionMs,
        bool autoPlay,
        CancellationToken cancellationToken,
        long? expectedGeneration = null,
        bool keepMutedAfterOpen = false,
        bool registerPlay = true)
    {
        var descriptor = _descriptor;
        if (descriptor is null) return;
        cancellationToken.ThrowIfCancellationRequested();
        var generation = expectedGeneration ?? Volatile.Read(ref _playbackGeneration);
        if (generation != Volatile.Read(ref _playbackGeneration)) return;

        var shouldRegisterPlay = false;
        var confirmAfterStart = autoPlay && logicalPositionMs > 0;
        var requestedVolume = Volume;
        var requestedSpeed = Speed;
        if (confirmAfterStart) _isSeeking = true;
        LocalPlaybackSegment? openedSegment = null;
        long localTargetMs = 0;

        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_descriptor, descriptor) || generation != Volatile.Read(ref _playbackGeneration)) return;

                var segment = FindSegment(logicalPositionMs);
                openedSegment = segment;
                localTargetMs = Math.Max(0, logicalPositionMs - segment.LogicalStartMs);
                _segment = segment;
                _segmentIndex = Array.FindIndex(descriptor.Segments.ToArray(), x => x.SegmentNumber == segment.SegmentNumber);
                PositionMs = Math.Clamp(logicalPositionMs, 0, Math.Max(0, DurationMs));
                StatusText = autoPlay ? "Opening local audio…" : "Preparing local audio…";
                RaisePropertyChanged(nameof(SegmentText));
            }, cancellationToken).ConfigureAwait(false);

            if (openedSegment is null || !ReferenceEquals(_descriptor, descriptor) ||
                generation != Volatile.Read(ref _playbackGeneration)) return;
            var segmentToOpen = openedSegment;

            var decoderWatch = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var segmentPath = segmentToOpen.MediaPath;
                await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(_descriptor, descriptor) ||
                        generation != Volatile.Read(ref _playbackGeneration)) return;

                    // Open first, then apply speed/volume/position while NAudio has no
                    // output device yet. Play creates the WaveOut pipeline exactly once
                    // at the final resume point.
                    _session.Open(segmentPath);
                    _session.SetSpeed(requestedSpeed);
                    _session.SetVolume(keepMutedAfterOpen || confirmAfterStart ? 0d : requestedVolume);
                    if (localTargetMs > 0)
                        _session.Seek(TimeSpan.FromMilliseconds(localTargetMs));
                    if (autoPlay)
                    {
                        _session.Play();
                        shouldRegisterPlay = true;
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                decoderWatch.Stop();
                await _dispatcher.InvokeAsync(() =>
                {
                    LastDecoderOpenDurationMs = decoderWatch.ElapsedMilliseconds;
                    RaisePropertyChanged(nameof(LastStartupTimingText));
                }).ConfigureAwait(false);
                RuntimeDiagnosticRecorder.Record(
                    "playback",
                    "open-local-decoder",
                    _session.Status == PlaybackStatus.Failed ? "failed" : "passed",
                    decoderWatch.ElapsedMilliseconds,
                    $"Opened {Path.GetExtension(segmentToOpen.MediaPath)} audio at {localTargetMs} ms.",
                    new Dictionary<string, string>
                    {
                        ["episodeId"] = descriptor.RepresentativeEpisodeId.ToString(),
                        ["segment"] = segmentToOpen.SegmentNumber.ToString(),
                        ["autoPlay"] = autoPlay.ToString(),
                        ["resumeMs"] = localTargetMs.ToString()
                    });
            }

            // Confirm the requested position after the output pipeline is active.
            if (confirmAfterStart && openedSegment is not null)
            {
                await ConfirmLogicalPositionAsync(
                    descriptor,
                    openedSegment,
                    logicalPositionMs,
                    generation,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (confirmAfterStart)
            {
                await _dispatcher.InvokeAsync(() =>
                {
                    if (ReferenceEquals(_descriptor, descriptor) && generation == Volatile.Read(ref _playbackGeneration))
                    {
                        if (!keepMutedAfterOpen) _session.SetVolume(Volume);
                        PositionMs = Math.Clamp(logicalPositionMs, 0, Math.Max(0, DurationMs));
                    }
                    _isSeeking = false;
                }, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (shouldRegisterPlay && registerPlay)
            _ = RegisterPlayAsync();
    }

    private async Task ConfirmLogicalPositionAsync(
        LocalPlaybackDescriptor descriptor,
        LocalPlaybackSegment segment,
        long logicalPositionMs,
        long generation,
        CancellationToken cancellationToken)
    {
        var localTargetMs = Math.Max(0, logicalPositionMs - segment.LogicalStartMs);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await Task.Delay(attempt == 0 ? 120 : 90, cancellationToken).ConfigureAwait(false);
            if (!ReferenceEquals(_descriptor, descriptor) || generation != Volatile.Read(ref _playbackGeneration)) return;
            if (_session.BroadcastId != descriptor.RepresentativeEpisodeId ||
                !MediaPathMatches(_session.MediaPath, segment.MediaPath)) return;

            var observedMs = Math.Max(0, (long)_session.Position.TotalMilliseconds);
            if (Math.Abs(observedMs - localTargetMs) <= 1_500) return;
            _session.Seek(TimeSpan.FromMilliseconds(localTargetMs));
        }
    }

    private async Task AlignPreparedDecoderAsync(
        LocalPlaybackDescriptor descriptor,
        long logicalPositionMs,
        long generation,
        bool desiredPlaying,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = FindSegment(logicalPositionMs);
        var current = _segment;
        var canSeekPreparedDecoder = current is not null &&
            current.SegmentNumber == target.SegmentNumber &&
            _session.BroadcastId == descriptor.RepresentativeEpisodeId &&
            MediaPathMatches(_session.MediaPath, current.MediaPath);
        if (!canSeekPreparedDecoder)
        {
            await OpenLogicalPositionAsync(
                logicalPositionMs,
                autoPlay: desiredPlaying,
                cancellationToken: cancellationToken,
                expectedGeneration: generation,
                keepMutedAfterOpen: desiredPlaying,
                registerPlay: false).ConfigureAwait(true);
            return;
        }

        var localTargetMs = Math.Max(0, logicalPositionMs - target.LogicalStartMs);
        _isSeeking = true;
        try
        {
            PositionMs = Math.Clamp(logicalPositionMs, 0, Math.Max(0, DurationMs));
            _session.Seek(TimeSpan.FromMilliseconds(localTargetMs));
            if (desiredPlaying)
            {
                _session.SetVolume(0d);
                if (!_session.IsPlaying) _session.Play();
            }
            else if (_session.IsPlaying)
            {
                _session.Pause();
            }

            await ConfirmLogicalPositionAsync(
                descriptor,
                target,
                logicalPositionMs,
                generation,
                cancellationToken).ConfigureAwait(true);
            PositionMs = Math.Clamp(logicalPositionMs, 0, Math.Max(0, DurationMs));
        }
        finally
        {
            _isSeeking = false;
        }
    }

    private async Task WaitForPreparedDecoderAsync(
        LocalPlaybackDescriptor descriptor,
        long generation,
        bool desiredPlaying,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.Add(PlaybackTransferDecoderReadyTimeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_descriptor, descriptor) ||
                generation != Volatile.Read(ref _playbackGeneration))
            {
                throw new InvalidOperationException(
                    "Playback changed before the prepared decoder became ready.");
            }

            if (_session.Status == PlaybackStatus.Failed)
                throw new InvalidOperationException(
                    "The target playback engine failed while preparing the broadcast.");

            if (desiredPlaying)
            {
                if (_session.Status == PlaybackStatus.Playing && _session.IsPlaying)
                {
                    // AVFoundation can briefly expose Playing while it is still
                    // transitioning out of Buffering. Require a second stable sample
                    // before telling the server that the muted target is runnable.
                    await Task.Delay(TimeSpan.FromMilliseconds(150), cancellationToken).ConfigureAwait(true);
                    if (_session.Status == PlaybackStatus.Playing && _session.IsPlaying)
                        return;
                }
                else if (_session.Status == PlaybackStatus.Paused)
                {
                    _session.SetVolume(0d);
                    _session.Play();
                }
            }
            else if (_session.Status == PlaybackStatus.Paused)
            {
                return;
            }
            else if (_session.Status == PlaybackStatus.Playing)
            {
                _session.Pause();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(true);
        }

        throw new InvalidOperationException(
            $"The target playback engine remained {_session.Status.ToString().ToLowerInvariant()} while preparing the handoff.");
    }

    private LocalPlaybackSegment FindSegment(long logicalPositionMs)
    {
        if (_descriptor is null || _descriptor.Segments.Count == 0)
            throw new InvalidOperationException("No local playback plan is loaded.");
        return _descriptor.Segments.FirstOrDefault(segment =>
                   logicalPositionMs >= segment.LogicalStartMs && logicalPositionMs < segment.LogicalEndMs)
               ?? _descriptor.Segments.Last();
    }

    private void SessionOnStateChanged(object? sender, PlaybackSessionSnapshot snapshot)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            var descriptor = _descriptor;
            var segment = _segment;
            if (descriptor is null || segment is null) return;
            if (snapshot.BroadcastId != descriptor.RepresentativeEpisodeId) return;
            if (!MediaPathMatches(snapshot.MediaPath, segment.MediaPath)) return;

            IsPlaying = snapshot.IsPlaying;
            if (!_isSeeking)
            {
                PositionMs = Math.Clamp(
                    segment.LogicalStartMs + (long)snapshot.Position.TotalMilliseconds,
                    0,
                    Math.Max(0, DurationMs));
            }
            if (snapshot.Duration is TimeSpan physicalDuration && segment.LogicalEndMs <= segment.LogicalStartMs)
                DurationMs = Math.Max(DurationMs, segment.LogicalStartMs + (long)physicalDuration.TotalMilliseconds);
            StatusText = snapshot.Status switch
            {
                PlaybackStatus.Opening => "Opening audio…",
                PlaybackStatus.Buffering => "Buffering…",
                PlaybackStatus.Failed => "Playback failed",
                PlaybackStatus.Paused => IsLoaded ? "Paused" : StatusText,
                PlaybackStatus.Playing => IsMultipart ? SegmentText : $"Playing from the {_playbackSourceText}",
                _ => StatusText
            };

            if (_completion.Observe(PositionMs, DurationMs, IsPlaying, _isSeeking, completionThresholdSeconds: _completionThresholdSeconds))
            {
                _completion.MarkCompleted();
                _completed = true;
                _completionResetPending = false;
                PublishLiveProgress();
                _ = SaveProgressAsync(false);
            }
        });
    }

    private void SessionOnMediaEnded(object? sender, EventArgs e)
        => _ = HandleMediaEndedAsync();

    private async Task HandleMediaEndedAsync()
    {
        try
        {
            LocalPlaybackSegment? next = null;
            await _dispatcher.InvokeAsync(() =>
            {
                if (_descriptor is null) return;
                if (_segmentIndex + 1 < _descriptor.Segments.Count)
                {
                    next = _descriptor.Segments[_segmentIndex + 1];
                    PositionMs = next.LogicalStartMs;
                    StatusText = $"Opening part {next.SegmentNumber}…";
                    return;
                }

                PositionMs = DurationMs;
                _completed = true;
                _completionResetPending = false;
                _completion.MarkCompleted();
                IsPlaying = false;
                PublishLiveProgress();
                StatusText = "Completed";
            }).ConfigureAwait(false);

            if (next is not null)
            {
                await OpenLogicalPositionAsync(next.LogicalStartMs, autoPlay: true, CancellationToken.None).ConfigureAwait(false);
                await _dispatcher.InvokeAsync(() => StatusText = SegmentText).ConfigureAwait(false);
                await SaveProgressAsync(false, CancellationToken.None, waitForGate: true).ConfigureAwait(false);
                return;
            }

            await SaveProgressAsync(false, CancellationToken.None, waitForGate: true).ConfigureAwait(false);
            await TryAdvanceQueueAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _dispatcher.InvokeAsync(() =>
            {
                IsPlaying = false;
                StatusText = $"The next audio item could not be opened: {exception.Message}";
            }).ConfigureAwait(false);
        }
    }

    private async Task TryAdvanceQueueAsync()
    {
        var queue = await _queue.GetAsync().ConfigureAwait(false);
        var next = queue.FirstOrDefault();
        if (next is null) return;
        await _dispatcher.InvokeAsync(() => StatusText = "Opening the next queued broadcast…").ConfigureAwait(false);
        await LoadAndPlayAsync(next.BroadcastId, CancellationToken.None).ConfigureAwait(false);
        if (IsLoaded && CurrentBroadcastId == next.BroadcastId)
        {
            await _queue.RemoveAsync(next.Id).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => QueueChanged?.Invoke(this, EventArgs.Empty)).ConfigureAwait(false);
        }
    }

    private void SessionOnMediaFailed(object? sender, PlaybackErrorEventArgs e)
    {
        _ = _dispatcher.InvokeAsync(() =>
        {
            IsPlaying = false;
            StatusText = $"Playback failed: {e.ErrorException.Message}";
        });
    }

    private async Task RegisterPlayAsync()
    {
        if (!_playCountPending || _descriptor is null) return;
        await SaveProgressAsync(incrementPlayCount: true, CancellationToken.None, waitForGate: true).ConfigureAwait(false);
        _playCountPending = false;
    }

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _shutdownPersistenceCompleted) != 0) return;
        if (Volatile.Read(ref _shutdownPersistenceActive) != 0)
        {
            await _library.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await SaveProgressAsync(false, cancellationToken, waitForGate: true).ConfigureAwait(false);
        await _library.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PauseAndFlushAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _shutdownPersistenceActive, 1) != 0)
        {
            await _library.FlushAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        // Stop every periodic writer before taking the final snapshot. Without this,
        // a five-second timer callback can begin after the final save and replace it
        // with an older decoder position while the window is closing.
        _saveTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _handoffTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _handoffReportTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _remoteProjectionTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        CancelActiveSeek();

        long frozenPositionMs = PositionMs;
        if (_dispatcher.CheckAccess())
        {
            if (_descriptor is not null)
                frozenPositionMs = CaptureCurrentLogicalPosition(_descriptor);
            if (_session.IsPlaying) _session.Pause();
            IsPlaying = false;
            PositionMs = frozenPositionMs;
        }
        else
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (_descriptor is not null)
                    frozenPositionMs = CaptureCurrentLogicalPosition(_descriptor);
                if (_session.IsPlaying) _session.Pause();
                IsPlaying = false;
                PositionMs = frozenPositionMs;
            }, cancellationToken).ConfigureAwait(false);
        }

        // Freeze the last trustworthy logical position before shutdown. This avoids
        // a late decoder/buffer snapshot replacing the visible position with the
        // value from which the session originally opened.
        await SaveProgressAsync(
            false,
            cancellationToken,
            waitForGate: true,
            positionOverrideMs: frozenPositionMs,
            isFinalFlush: true,
            throwOnFailure: true).ConfigureAwait(false);
        await _library.FlushAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _shutdownPersistenceCompleted, 1);
    }

    private async Task SaveProgressAsync(
        bool incrementPlayCount,
        CancellationToken cancellationToken = default,
        bool waitForGate = false,
        long? positionOverrideMs = null,
        bool explicitSeek = false,
        bool isFinalFlush = false,
        bool throwOnFailure = false)
    {
        var descriptor = _descriptor;
        var generation = Volatile.Read(ref _playbackGeneration);
        if (descriptor is null || _disposed) return;
        if (_handoff.IsAvailable && _handoffSnapshot?.IsOwnedByCurrentDevice == false) return;
        if (!isFinalFlush && Volatile.Read(ref _shutdownPersistenceActive) != 0) return;
        if (!waitForGate && (IsBusy || _isSeeking)) return;

        var entered = waitForGate
            ? await WaitForSaveGateAsync(cancellationToken).ConfigureAwait(false)
            : await _saveGate.WaitAsync(0, cancellationToken).ConfigureAwait(false);
        if (!entered) return;
        try
        {
            if (!ReferenceEquals(descriptor, _descriptor) || generation != Volatile.Read(ref _playbackGeneration)) return;

            var currentPositionMs = positionOverrideMs.HasValue
                ? Math.Clamp(positionOverrideMs.Value, 0, Math.Max(0, DurationMs))
                : CaptureCurrentLogicalPosition(descriptor);
            var plan = _progress.CreatePlan(new PlaybackProgressRequest(
                ReportedPositionMs: currentPositionMs,
                EpisodePositionMs: descriptor.ResumePositionMs,
                PlayerStatePositionMs: currentPositionMs,
                LastObservedPositionMs: _completion.LastObservedPositionMs,
                LogicalResumePositionMs: currentPositionMs,
                ReportedDurationMs: DurationMs,
                EpisodeDurationMs: descriptor.DurationMs,
                Completed: _completed,
                Speed: Speed,
                IncrementPlayCount: incrementPlayCount));
            var allowCompletionReset = _completionResetPending && !plan.Completed;

            if (_handoff.IsAvailable)
            {
                try
                {
                    // Current servers use the ownership report to reject a stale writer before
                    // the canonical progress mutation. A 0.31 server is detected by the handoff
                    // adapter and drops into progress-only compatibility before reaching here.
                    await _handoff.ReportAsync(descriptor.RepresentativeEpisodeId,
                        plan.PositionMs, plan.DurationMs, plan.Speed, IsPlaying, plan.Completed,
                        explicitSeek: explicitSeek,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    // A genuine ownership conflict on a current server must still block this
                    // device's write. Refresh the owner state so the transfer action becomes visible.
                    await RefreshHandoffAsync().ConfigureAwait(false);
                    return;
                }
                catch (Exception exception)
                {
                    // Handoff telemetry is supplementary to the established progress endpoint.
                    // A transient or version-specific handoff failure must never prevent the same
                    // canonical write that the accepted 0.31 client has always used.
                    System.Diagnostics.Trace.WriteLine($"[Avalonia playback handoff] Ownership report failed; continuing with canonical progress sync. {exception}");
                }
            }

            // A timer write that began for the outgoing broadcast may finish after a
            // new item is selected. Re-check identity immediately before persistence
            // so a position can never cross the broadcast boundary.
            if (!ReferenceEquals(descriptor, _descriptor) || generation != Volatile.Read(ref _playbackGeneration)) return;
            await _library.SaveAsync(new LocalPlaybackSaveRequest(
                descriptor.CanonicalKey,
                descriptor.RepresentativeEpisodeId,
                plan.PositionMs,
                plan.DurationMs,
                plan.Completed,
                plan.Speed,
                plan.IncrementPlayCount,
                allowCompletionReset,
                _handoff.IsAvailable ? _handoff.CurrentGeneration : 0,
                explicitSeek || allowCompletionReset), cancellationToken).ConfigureAwait(false);

            if (allowCompletionReset && ReferenceEquals(descriptor, _descriptor) && generation == Volatile.Read(ref _playbackGeneration))
                _completionResetPending = false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (throwOnFailure) throw;
        }
        catch (Exception exception)
        {
            await _dispatcher.InvokeAsync(() => StatusText = $"Progress could not be saved: {exception.Message}").ConfigureAwait(false);
            if (throwOnFailure) throw;
        }
        finally
        {
            _saveGate.Release();
        }
    }

    private async Task<bool> WaitForSaveGateAsync(CancellationToken cancellationToken)
    {
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private long CaptureCurrentLogicalPosition(LocalPlaybackDescriptor descriptor)
    {
        var observed = Math.Clamp(
            Volatile.Read(ref _latestObservedPositionMs),
            0,
            Math.Max(0, DurationMs));
        var segment = _segment;
        if (segment is null) return observed;
        if (_session.BroadcastId != descriptor.RepresentativeEpisodeId) return observed;
        if (!MediaPathMatches(_session.MediaPath, segment.MediaPath)) return observed;

        var engineLogical = Math.Clamp(
            segment.LogicalStartMs + (long)_session.Position.TotalMilliseconds,
            0,
            Math.Max(0, DurationMs));

        // PositionMs is fed by the engine's periodic state snapshots and by an
        // explicit seek target. If a one-off engine read suddenly disagrees by more
        // than a few seconds, keep the continuously observed value rather than
        // persisting an old decoder position at shutdown.
        return Math.Abs(engineLogical - observed) > 3_000 ? observed : engineLogical;
    }

    private static bool MediaPathMatches(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right)) return false;
        if (Uri.TryCreate(left, UriKind.Absolute, out var leftUri)
            && (leftUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || leftUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            && Uri.TryCreate(right, UriKind.Absolute, out var rightUri))
        {
            return string.Equals(leftUri.AbsoluteUri, rightUri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        }
        try
        {
            return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private async Task ReportHandoffProgressAsync()
    {
        if (_disposed || !_handoff.IsAvailable || IsBusy || IsPlaybackElsewhere || !IsLoaded ||
            _handoffSnapshot?.IsOwnedByCurrentDevice != true) return;
        var descriptor = _descriptor;
        if (descriptor is null) return;
        if (!await _handoffReportGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var position = CaptureCurrentLogicalPosition(descriptor);
            await _handoff.ReportAsync(
                descriptor.RepresentativeEpisodeId,
                position,
                DurationMs,
                Speed,
                IsPlaying,
                _completed,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            await RefreshHandoffAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            System.Diagnostics.Trace.WriteLine($"[Avalonia playback handoff] Live heartbeat failed: {exception}");
        }
        finally
        {
            _handoffReportGate.Release();
        }
    }

    private async Task RefreshHandoffAsync()
    {
        if (_disposed || !_handoff.IsAvailable) return;
        if (!await _handoffRefreshGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            var snapshot = await _handoff.GetSnapshotAsync().ConfigureAwait(false);
            if (snapshot is null) return;

            // A committed source must physically stop before it acknowledges. This
            // path runs even while the view model is otherwise busy so an outgoing
            // decoder cannot continue audibly behind a newly committed target.
            if (await TryAcknowledgeSourceStopAsync(snapshot).ConfigureAwait(false))
            {
                snapshot = await _handoff.GetSnapshotAsync().ConfigureAwait(false) ?? snapshot;
            }

            if (IsBusy) return;
            await _dispatcher.InvokeAsync(() => ApplyHandoffSnapshot(snapshot)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Handoff status is opportunistic; normal local playback remains
            // available during outages. Source-stop acknowledgement retries on the
            // next poll and is generation-bound, so duplicate attempts are harmless.
            System.Diagnostics.Trace.WriteLine($"[Avalonia playback handoff] Session refresh failed: {exception}");
        }
        finally
        {
            _handoffRefreshGate.Release();
        }
    }

    private async Task<bool> TryAcknowledgeSourceStopAsync(PlaybackHandoffSnapshot snapshot)
    {
        var receipt = snapshot.CommittedTransfer;
        if (receipt is null || receipt.SourceStopAcknowledged || !receipt.SourceWasPlaying ||
            string.IsNullOrWhiteSpace(_handoff.CurrentDeviceId) ||
            !string.Equals(receipt.SourceClientId, _handoff.CurrentDeviceId, StringComparison.Ordinal) ||
            string.Equals(receipt.TargetClientId, _handoff.CurrentDeviceId, StringComparison.Ordinal))
            return false;
        if (Interlocked.CompareExchange(ref _sourceStopAcknowledgementActive, 1, 0) != 0)
            return false;

        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                _desiredPlaying = false;
                _transportIntentChanged = false;
                if (_session.IsPlaying) _session.Pause();
                IsPlaying = false;
                IsPlaybackElsewhere = true;
                StatusText = $"Playback moved to {receipt.TargetDeviceName}.";
                RaisePrimaryTransportState();
            }).ConfigureAwait(false);

            await _handoff.AcknowledgeSourceStoppedAsync(
                receipt.TransferId, receipt.Generation, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _sourceStopAcknowledgementActive, 0);
        }
    }


    private async Task EnsureCommittedOwnershipAsync(
        long committedGeneration,
        CancellationToken cancellationToken)
    {
        PlaybackHandoffSnapshot? latest;
        try
        {
            latest = await _handoff.GetSnapshotAsync(cancellationToken).ConfigureAwait(true);
        }
        catch
        {
            // The commit itself is authoritative. A transient status-read outage must
            // not create silence; the source-stop wait already bounds disconnected
            // sources and the normal heartbeat loop will reconcile when connectivity
            // returns.
            return;
        }

        if (latest is null) return;
        _handoffSnapshot = latest;
        if (latest.Generation != committedGeneration || !latest.IsOwnedByCurrentDevice)
            throw new PlaybackOwnershipMovedException(
                $"Playback moved again to {latest.OwnerDeviceName} before this device could start.");
    }

    private async Task EnsureTargetOutputStateAsync(
        bool desiredPlaying,
        double audibleVolume,
        CancellationToken cancellationToken)
    {
        _session.SetVolume(audibleVolume);
        if (!desiredPlaying)
        {
            if (_session.IsPlaying) _session.Pause();
            return;
        }

        // Decoder preparation may report success a fraction before the playback
        // engine exposes its final running state. Re-issue Play across a short,
        // bounded window and fail loudly only when the engine genuinely cannot
        // leave Opening/Buffering/Paused. This prevents a committed transfer from
        // looking successful while the new output remains silent.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_session.IsPlaying) _session.Play();
            await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken).ConfigureAwait(true);
            if (_session.IsPlaying) return;
            if (_session.Status == PlaybackStatus.Failed)
                throw new InvalidOperationException("The target playback engine failed after ownership was committed.");
        }

        throw new InvalidOperationException(
            $"The target playback engine remained {_session.Status.ToString().ToLowerInvariant()} after ownership was committed.");
    }

    private async Task<bool> WaitForPreviousOutputToStopAsync(
        PlaybackHandoffSnapshot committedSnapshot,
        CancellationToken cancellationToken)
    {
        var receipt = committedSnapshot.CommittedTransfer;
        if (receipt is null || receipt.SourceStopAcknowledged || !receipt.SourceWasPlaying ||
            string.IsNullOrWhiteSpace(receipt.SourceClientId) ||
            string.Equals(receipt.SourceClientId, _handoff.CurrentDeviceId, StringComparison.Ordinal))
            return true;

        StatusText = $"Waiting for {receipt.SourceDeviceName} to stop…";
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(125, cancellationToken).ConfigureAwait(true);
            PlaybackHandoffSnapshot? snapshot;
            try
            {
                snapshot = await _handoff.GetSnapshotAsync(cancellationToken).ConfigureAwait(true);
            }
            catch
            {
                continue;
            }

            if (snapshot is null) continue;
            _handoffSnapshot = snapshot;
            if (snapshot.Generation != receipt.Generation || !snapshot.IsOwnedByCurrentDevice)
                throw new PlaybackOwnershipMovedException(
                    $"Playback moved again to {snapshot.OwnerDeviceName} before this device could start.");

            var current = snapshot.CommittedTransfer;
            if (current is not null && current.TransferId == receipt.TransferId &&
                current.Generation == receipt.Generation && current.SourceStopAcknowledged)
                return true;
        }

        // One final ownership read prevents a rapid second handoff from making an
        // older target audible immediately after its receipt wait times out.
        try
        {
            var latest = await _handoff.GetSnapshotAsync(cancellationToken).ConfigureAwait(true);
            if (latest is not null)
            {
                _handoffSnapshot = latest;
                if (latest.Generation != receipt.Generation || !latest.IsOwnedByCurrentDevice)
                    throw new PlaybackOwnershipMovedException(
                        $"Playback moved again to {latest.OwnerDeviceName} before this device could start.");
            }
        }
        catch (PlaybackOwnershipMovedException)
        {
            throw;
        }
        catch
        {
            // A disconnected source is exactly why the bounded safety timeout exists.
        }

        return false;
    }

    private void ApplyHandoffSnapshot(PlaybackHandoffSnapshot snapshot)
    {
        _handoffSnapshot = snapshot;
        HandoffDevices.Clear();
        foreach (var device in snapshot.Devices)
            HandoffDevices.Add(device);
        RaisePropertyChanged(nameof(IsHandoffAvailable));
        RaisePropertyChanged(nameof(HasHandoffDevices));
        RaisePropertyChanged(nameof(CanMovePlaybackToServer));
        var active = snapshot.ActivePlayback;
        IsPlaybackElsewhere = snapshot.IsPlayingElsewhere;
        if (snapshot.IsPlayingElsewhere && IsPlaying)
        {
            _session.Pause();
            IsPlaying = false;
            StatusText = $"Playback moved to {snapshot.OwnerDeviceName}.";
        }
        ActiveDeviceText = active is null
            ? "No shared playback session"
            : $"{(active.IsPlaying ? "Playing" : "Paused")} on " +
              (snapshot.IsOwnedByCurrentDevice ? snapshot.CurrentDeviceName : snapshot.OwnerDeviceName);
        if (active is null)
        {
            ActiveDeviceDetail = string.Empty;
        }
        else if (snapshot.IsPlayingElsewhere)
        {
            ApplyProjectedRemoteState(active);
        }
        else
        {
            ActiveDeviceDetail = $"{active.Title} · {FormatTime(active.ProjectedPositionMs(DateTimeOffset.UtcNow))}";
        }
        RaisePropertyChanged(nameof(ShowContinueOnThisDevice));
        RaisePrimaryTransportState();
        PublishLiveProgress();
    }

    private async Task UpdateProjectedRemoteStateAsync()
    {
        if (_disposed || !_handoff.IsAvailable) return;
        var snapshot = _handoffSnapshot;
        var active = snapshot?.ActivePlayback;
        if (snapshot?.IsPlayingElsewhere != true || active is null || !active.IsPlaying) return;

        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                if (!ReferenceEquals(snapshot, _handoffSnapshot) || _handoffSnapshot?.IsPlayingElsewhere != true) return;
                ApplyProjectedRemoteState(active);
            }).ConfigureAwait(false);
        }
        catch
        {
            // The projection timer is presentation-only and may race application shutdown.
        }
    }

    private void ApplyProjectedRemoteState(PlaybackDeviceState active)
    {
        var projectedPosition = active.ProjectedPositionMs(DateTimeOffset.UtcNow);
        var snapshot = _handoffSnapshot;
        var sameRemoteRun = active.IsPlaying &&
            active.RepresentativeEpisodeId == _remoteProjectionEpisodeId &&
            snapshot?.Generation == _remoteProjectionGeneration &&
            string.Equals(snapshot.OwnerDeviceId, _remoteProjectionOwnerId, StringComparison.Ordinal);

        // Network heartbeats arrive around once a second and may be a few hundred
        // milliseconds behind the locally projected sample. Keep the remote Mac
        // playhead monotonic within one ownership generation so it does not visibly
        // twitch backwards while an iPhone is playing. A larger backwards movement
        // is treated as an intentional remote seek; a new handoff or broadcast also
        // establishes a fresh projection baseline.
        if (sameRemoteRun && projectedPosition >= _remoteProjectionPositionMs - 3_000)
            projectedPosition = Math.Max(projectedPosition, _remoteProjectionPositionMs);

        _remoteProjectionEpisodeId = active.RepresentativeEpisodeId;
        _remoteProjectionGeneration = snapshot?.Generation ?? -1;
        _remoteProjectionOwnerId = snapshot?.OwnerDeviceId ?? string.Empty;
        _remoteProjectionPositionMs = projectedPosition;
        PositionMs = projectedPosition;
        if (active.DurationMs > 0) DurationMs = active.DurationMs;
        if (Math.Abs(Speed - active.Speed) >= 0.001d) Speed = active.Speed;
        ActiveDeviceDetail = $"{active.Title} · {FormatTime(projectedPosition)}";
        PublishLiveProgress();
    }

    private async Task ContinueOnThisDeviceAsync()
    {
        var snapshot = _handoffSnapshot;
        var active = snapshot?.ActivePlayback;
        if (active?.RepresentativeEpisodeId is not > 0 || IsBusy) return;

        var targetEpisodeId = active.RepresentativeEpisodeId.Value;
        var projectedPosition = active.ProjectedPositionMs(DateTimeOffset.UtcNow);
        StatusText = $"Preparing playback while {snapshot?.OwnerDeviceName ?? "the other device"} keeps playing…";

        await SaveProgressAsync(false, waitForGate: true).ConfigureAwait(true);
        await LoadAndPlayAtStateAsync(
            targetEpisodeId,
            projectedPosition,
            active.IsPlaying,
            forceTransactionalTransfer: true,
            skipOutgoingSave: true).ConfigureAwait(true);
        await RefreshHandoffAsync().ConfigureAwait(true);
    }

    private Task MoveToServerAsync()
    {
        StatusText = "Use the Move to this device control on the server. The target now prepares before ownership changes.";
        return Task.CompletedTask;
    }

    private void RaisePrimaryTransportState()
    {
        RaisePropertyChanged(nameof(IsPrimaryTransportLoading));
        RaisePropertyChanged(nameof(PrimaryTransportSpinnerAngle));
        RaisePropertyChanged(nameof(ShowMoveToThisDevice));
        RaisePropertyChanged(nameof(ShowLocalPlayPause));
        RaisePropertyChanged(nameof(ShowPlayIcon));
        RaisePropertyChanged(nameof(ShowPauseIcon));
        RaisePropertyChanged(nameof(PlayPauseGlyph));
        RaisePropertyChanged(nameof(PrimaryTransportToolTip));
        RaisePropertyChanged(nameof(CanControlLocalTransport));
        ((DelegateCommand)PlayPauseCommand).RaiseCanExecuteChanged();
    }

    private void RaiseCommandState()
    {
        RaisePropertyChanged(nameof(CanControlLocalTransport));
        ((DelegateCommand)PlayPauseCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)SkipBackCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)SkipForwardCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)StopCommand).RaiseCanExecuteChanged();
        ((DelegateCommand)CycleSpeedCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ToggleFavouriteCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)AddToQueueCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)PlayNextCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ContinueOnThisDeviceCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)MoveToServerCommand).RaiseCanExecuteChanged();
    }

    private void RaiseCurrentBroadcastState()
    {
        RaisePropertyChanged(nameof(CurrentBroadcastId));
        RaisePropertyChanged(nameof(CurrentCanonicalKey));
        RaisePropertyChanged(nameof(HasCurrentBroadcast));
        RaisePropertyChanged(nameof(CanMovePlaybackToServer));
        PublishLiveProgress();
    }

    private void PublishLiveProgress()
    {
        PlaybackLiveProgress? next = null;
        var active = _handoffSnapshot?.ActivePlayback;

        // The decoder in this process is the most accurate source while this
        // endpoint owns playback. Other devices are projected from their latest
        // server-confirmed timestamp so Library and Dashboard rows move smoothly
        // between one-second authoritative heartbeats.
        if (_descriptor is not null && IsLoaded && !IsPlaybackElsewhere)
        {
            next = new PlaybackLiveProgress(
                _descriptor.RepresentativeEpisodeId,
                _descriptor.CanonicalKey,
                Math.Max(0, PositionMs),
                Math.Max(0, DurationMs),
                _completed || (DurationMs > 0 && PositionMs >= DurationMs - 5_000),
                IsPlaying,
                true,
                DateTimeOffset.UtcNow);
        }
        else if (active?.RepresentativeEpisodeId is > 0)
        {
            var duration = Math.Max(0, active.DurationMs);
            var position = active.ProjectedPositionMs(DateTimeOffset.UtcNow);
            next = new PlaybackLiveProgress(
                active.RepresentativeEpisodeId.Value,
                string.Empty,
                position,
                duration,
                duration > 0 && position >= duration - 5_000,
                active.IsPlaying,
                _handoffSnapshot?.IsOwnedByCurrentDevice == true,
                DateTimeOffset.UtcNow);
        }

        LiveProgress = next;
    }

    private sealed class PlaybackOwnershipMovedException : InvalidOperationException
    {
        public PlaybackOwnershipMovedException(string message) : base(message) { }
    }

    private void SetCommandError(Exception exception) => StatusText = exception.Message;

    private static string BuildSubtitle(LocalPlaybackDescriptor descriptor)
    {
        var date = descriptor.AirDate?.ToString("ddd, d MMM yyyy") ?? "Date unknown";
        return $"{descriptor.CollectionName} · {date}";
    }

    private async Task AdvanceLoadingGlyphAsync()
    {
        if (_disposed || !IsPrimaryTransportLoading) return;
        try
        {
            await _dispatcher.InvokeAsync(() =>
            {
                _loadingGlyphFrame = (_loadingGlyphFrame + 1) & 3;
                RaisePropertyChanged(nameof(PrimaryTransportSpinnerAngle));
            }).ConfigureAwait(false);
        }
        catch
        {
            // Presentation animation may race application shutdown.
        }
    }

    private static string NormalizeDeviceName(string? value)
        => string.IsNullOrWhiteSpace(value) ? "the other device" : value.Trim();

    private static string FormatTime(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelActiveSeek();
        _saveTimer.Dispose();
        _handoffTimer.Dispose();
        _handoffReportTimer.Dispose();
        _remoteProjectionTimer.Dispose();
        _loadingGlyphTimer.Dispose();
        _session.StateChanged -= SessionOnStateChanged;
        _session.MediaEnded -= SessionOnMediaEnded;
        _session.MediaFailed -= SessionOnMediaFailed;
        _session.Dispose();
        _saveGate.Dispose();
        _handoffReportGate.Dispose();
        _handoffRefreshGate.Dispose();
    }
}

public sealed record PlaybackFavouriteChangedEventArgs(long RepresentativeEpisodeId, bool Favourite);
