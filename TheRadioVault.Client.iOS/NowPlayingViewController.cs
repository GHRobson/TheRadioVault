using TheRadioVault.Client.Mobile;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class NowPlayingViewController : UIViewController
{
    private readonly MobileClientSession _session;
    private readonly UILabel _titleLabel = new();
    private readonly UILabel _subtitleLabel = new();
    private readonly UILabel _statusLabel = new();
    private readonly UILabel _elapsedLabel = new();
    private readonly UILabel _remainingLabel = new();
    private readonly UILabel _totalLabel = new();
    private readonly UIView _artworkPanel = new();
    private readonly UIImageView _artworkIcon = new();
    private readonly UISlider _progressSlider = new();
    private readonly UIButton _backButton = UIButton.FromType(UIButtonType.System);
    private readonly UIButton _playButton = UIButton.FromType(UIButtonType.System);
    private readonly UIButton _forwardButton = UIButton.FromType(UIButtonType.System);
    private readonly UIButton _speedButton = UIButton.FromType(UIButtonType.System);
    private readonly UIButton _momentButton = UIButton.FromType(UIButtonType.System);
    private readonly UIButton _infoButton = UIButton.FromType(UIButtonType.System);
    private readonly UIButton _favouriteButton = UIButton.FromType(UIButtonType.System);
    private readonly RadioVaultOutputRouteView _outputRoute = new();
    private readonly UIActivityIndicatorView _playActivity = new(UIActivityIndicatorViewStyle.Large);
    private readonly NowPlayingUpNextView _upNext;
    private bool _isScrubbing;
    private bool _favouriteSaving;
    private bool _momentSaving;
    private bool _momentSavedFeedback;
    private long _artworkEpisodeId;
    private bool _artworkWasRequestedOnline;

    public NowPlayingViewController(MobileClientSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _upNext = new NowPlayingUpNextView(_session);
        Title = "Now Playing";
        _session.StateChanged += SessionOnStateChanged;
        _session.PlaybackStateChanged += SessionOnStateChanged;
    }

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        if (View is not { } view) return;
        view.BackgroundColor = RadioVaultTheme.Background;
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        NavigationItem.Title = string.Empty;
        NavigationItem.BackButtonDisplayMode = UINavigationItemBackButtonDisplayMode.Minimal;

        _artworkPanel.BackgroundColor = RadioVaultTheme.Surface;
        _artworkPanel.Layer.CornerRadius = 24;
        _artworkPanel.Layer.MasksToBounds = true;
        _artworkIcon.Image = RadioVaultIcons.Image(RadioVaultIcon.Radio, size: 96, strokeWidth: 1.6f);
        _artworkIcon.ContentMode = UIViewContentMode.Center;
        _artworkIcon.TranslatesAutoresizingMaskIntoConstraints = false;
        _artworkPanel.AddSubview(_artworkIcon);
        NSLayoutConstraint.ActivateConstraints([
            _artworkIcon.LeadingAnchor.ConstraintEqualTo(_artworkPanel.LeadingAnchor),
            _artworkIcon.TrailingAnchor.ConstraintEqualTo(_artworkPanel.TrailingAnchor),
            _artworkIcon.TopAnchor.ConstraintEqualTo(_artworkPanel.TopAnchor),
            _artworkIcon.BottomAnchor.ConstraintEqualTo(_artworkPanel.BottomAnchor)
        ]);
        var artworkStage = new UIView();
        _artworkPanel.TranslatesAutoresizingMaskIntoConstraints = false;
        artworkStage.AddSubview(_artworkPanel);
        NSLayoutConstraint.ActivateConstraints([
            _artworkPanel.CenterXAnchor.ConstraintEqualTo(artworkStage.CenterXAnchor),
            _artworkPanel.CenterYAnchor.ConstraintEqualTo(artworkStage.CenterYAnchor),
            _artworkPanel.WidthAnchor.ConstraintEqualTo(240),
            _artworkPanel.HeightAnchor.ConstraintEqualTo(_artworkPanel.WidthAnchor),
            artworkStage.HeightAnchor.ConstraintEqualTo(240)
        ]);

        _titleLabel.Font = (UIFont.PreferredTitle2 ?? UIFont.SystemFontOfSize(24, UIFontWeight.Bold))!;
        _titleLabel.TextColor = RadioVaultTheme.Text;
        _titleLabel.Lines = 0;
        _titleLabel.TextAlignment = UITextAlignment.Center;
        _titleLabel.AdjustsFontForContentSizeCategory = true;
        _subtitleLabel.Font = (UIFont.PreferredBody ?? UIFont.SystemFontOfSize(17))!;
        _subtitleLabel.TextColor = RadioVaultTheme.MutedText;
        _subtitleLabel.Lines = 0;
        _subtitleLabel.TextAlignment = UITextAlignment.Center;
        _subtitleLabel.AdjustsFontForContentSizeCategory = true;
        _statusLabel.Font = (UIFont.PreferredFootnote ?? UIFont.SystemFontOfSize(13))!;
        _statusLabel.TextColor = RadioVaultTheme.MutedText;
        _statusLabel.Lines = 0;
        _statusLabel.TextAlignment = UITextAlignment.Center;
        _statusLabel.AdjustsFontForContentSizeCategory = true;
        ConfigureTimeLabel(_elapsedLabel, UITextAlignment.Left);
        ConfigureTimeLabel(_remainingLabel, UITextAlignment.Right);
        ConfigureTimeLabel(_totalLabel, UITextAlignment.Center);

        _progressSlider.MinValue = 0;
        _progressSlider.MaxValue = 1;
        _progressSlider.Continuous = true;
        _progressSlider.MinimumTrackTintColor = RadioVaultTheme.Accent;
        _progressSlider.MaximumTrackTintColor = RadioVaultTheme.Border;
        _progressSlider.ThumbTintColor = RadioVaultTheme.Accent;
        _progressSlider.AccessibilityLabel = "Playback position";
        _progressSlider.AccessibilityHint = "Swipe up or down to move through this broadcast";
        _progressSlider.TouchDown += (_, _) => _isScrubbing = true;
        _progressSlider.TouchUpInside += ProgressSliderFinished;
        _progressSlider.TouchUpOutside += ProgressSliderFinished;
        _progressSlider.TouchCancel += ProgressSliderFinished;

        ConfigureButton(_backButton, RadioVaultIcons.Image(RadioVaultIcon.SkipBack, RadioVaultTheme.Accent, 42, 1.75), "Back 15 seconds", _session.SkipBack);
        ConfigureButton(_playButton, RadioVaultIcons.Image(RadioVaultIcon.Play, RadioVaultTheme.Accent, 68, 1.5f), "Play or pause", _session.MiniPlayerAction);
        ConfigureButton(_forwardButton, RadioVaultIcons.Image(RadioVaultIcon.SkipForward, RadioVaultTheme.Accent, 42, 1.75), "Forward 30 seconds", _session.SkipForward);
        _playActivity.Color = RadioVaultTheme.Accent;
        _playActivity.HidesWhenStopped = true;
        _playActivity.TranslatesAutoresizingMaskIntoConstraints = false;
        _playButton.AddSubview(_playActivity);
        _backButton.TintColor = RadioVaultTheme.Accent;
        _forwardButton.TintColor = RadioVaultTheme.Accent;
        _speedButton.TouchUpInside += (_, _) => _session.CycleSpeed();
        _speedButton.TitleLabel!.Font = RadioVaultAccessibility.ScaledFont(16, UIFontWeight.Semibold);
        _speedButton.TitleLabel.AdjustsFontForContentSizeCategory = true;
        _speedButton.SetTitleColor(RadioVaultTheme.Text, UIControlState.Normal);
        _speedButton.BackgroundColor = RadioVaultTheme.SurfaceRaised;
        _speedButton.Layer.CornerRadius = 19;
        _speedButton.AccessibilityLabel = "Playback speed";

        ConfigureActionButton(_momentButton, "Moment", RadioVaultIcon.Moment, PresentMomentEditor);
        ConfigureActionButton(_infoButton, "Info", RadioVaultIcon.Info, OpenBroadcastInformation);
        ConfigureActionButton(_favouriteButton, "Favourite", RadioVaultIcon.Favourite, ToggleFavourite);

        var controls = new UIStackView([_backButton, _playButton, _forwardButton])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var controlsStage = new UIView();
        controlsStage.AddSubview(controls);
        var metadata = new UIStackView([_titleLabel, _subtitleLabel])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 6
        };
        var times = new UIView();
        _elapsedLabel.TranslatesAutoresizingMaskIntoConstraints = false;
        _totalLabel.TranslatesAutoresizingMaskIntoConstraints = false;
        _remainingLabel.TranslatesAutoresizingMaskIntoConstraints = false;
        times.AddSubviews(_elapsedLabel, _totalLabel, _remainingLabel);
        var progress = new UIStackView([_progressSlider, times])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 4
        };
        var actions = new UIStackView([_momentButton, _infoButton, _favouriteButton])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.FillEqually,
            Spacing = 10
        };
        _speedButton.TranslatesAutoresizingMaskIntoConstraints = false;
        var secondaryControls = new UIStackView([_speedButton, _outputRoute])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 10
        };
        var playerContent = new UIStackView([artworkStage, metadata, progress, controlsStage, actions, secondaryControls, _statusLabel])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 20
        };
        playerContent.SetCustomSpacing(28, artworkStage);
        playerContent.SetCustomSpacing(26, progress);
        playerContent.SetCustomSpacing(16, controlsStage);
        var content = new UIStackView([playerContent, _upNext])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 24,
            TranslatesAutoresizingMaskIntoConstraints = false
        };

        var scrollView = new UIScrollView
        {
            AlwaysBounceVertical = true,
            ShowsVerticalScrollIndicator = false,
            ShowsHorizontalScrollIndicator = false,
            DirectionalLockEnabled = true,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        var contentHost = new UIView
        {
            BackgroundColor = UIColor.Clear,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        view.AddSubview(scrollView);
        scrollView.AddSubview(contentHost);
        contentHost.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints([
            scrollView.LeadingAnchor.ConstraintEqualTo(view.SafeAreaLayoutGuide.LeadingAnchor),
            scrollView.TrailingAnchor.ConstraintEqualTo(view.SafeAreaLayoutGuide.TrailingAnchor),
            scrollView.TopAnchor.ConstraintEqualTo(view.SafeAreaLayoutGuide.TopAnchor),
            scrollView.BottomAnchor.ConstraintEqualTo(view.SafeAreaLayoutGuide.BottomAnchor),
            contentHost.LeadingAnchor.ConstraintEqualTo(scrollView.ContentLayoutGuide.LeadingAnchor),
            contentHost.TrailingAnchor.ConstraintEqualTo(scrollView.ContentLayoutGuide.TrailingAnchor),
            contentHost.TopAnchor.ConstraintEqualTo(scrollView.ContentLayoutGuide.TopAnchor),
            contentHost.BottomAnchor.ConstraintEqualTo(scrollView.ContentLayoutGuide.BottomAnchor),
            contentHost.WidthAnchor.ConstraintEqualTo(scrollView.FrameLayoutGuide.WidthAnchor),
            content.LeadingAnchor.ConstraintEqualTo(contentHost.LeadingAnchor, 20),
            content.TrailingAnchor.ConstraintEqualTo(contentHost.TrailingAnchor, -20),
            content.TopAnchor.ConstraintEqualTo(contentHost.TopAnchor, 16),
            content.BottomAnchor.ConstraintEqualTo(contentHost.BottomAnchor, -24),
            playerContent.HeightAnchor.ConstraintGreaterThanOrEqualTo(scrollView.FrameLayoutGuide.HeightAnchor, -40),
            times.HeightAnchor.ConstraintGreaterThanOrEqualTo(18),
            _elapsedLabel.LeadingAnchor.ConstraintEqualTo(times.LeadingAnchor),
            _elapsedLabel.TopAnchor.ConstraintEqualTo(times.TopAnchor),
            _elapsedLabel.BottomAnchor.ConstraintEqualTo(times.BottomAnchor),
            _totalLabel.CenterXAnchor.ConstraintEqualTo(times.CenterXAnchor),
            _totalLabel.TopAnchor.ConstraintEqualTo(times.TopAnchor),
            _totalLabel.BottomAnchor.ConstraintEqualTo(times.BottomAnchor),
            _remainingLabel.TrailingAnchor.ConstraintEqualTo(times.TrailingAnchor),
            _remainingLabel.TopAnchor.ConstraintEqualTo(times.TopAnchor),
            _remainingLabel.BottomAnchor.ConstraintEqualTo(times.BottomAnchor),
            controlsStage.HeightAnchor.ConstraintEqualTo(96),
            controls.CenterXAnchor.ConstraintEqualTo(controlsStage.CenterXAnchor),
            controls.CenterYAnchor.ConstraintEqualTo(controlsStage.CenterYAnchor),
            _backButton.WidthAnchor.ConstraintEqualTo(64),
            _backButton.HeightAnchor.ConstraintEqualTo(64),
            _playButton.WidthAnchor.ConstraintEqualTo(96),
            _playButton.HeightAnchor.ConstraintEqualTo(96),
            _playActivity.CenterXAnchor.ConstraintEqualTo(_playButton.CenterXAnchor),
            _playActivity.CenterYAnchor.ConstraintEqualTo(_playButton.CenterYAnchor),
            _forwardButton.WidthAnchor.ConstraintEqualTo(64),
            _forwardButton.HeightAnchor.ConstraintEqualTo(64),
            actions.HeightAnchor.ConstraintGreaterThanOrEqualTo(48),
            secondaryControls.HeightAnchor.ConstraintGreaterThanOrEqualTo(44),
            _speedButton.WidthAnchor.ConstraintEqualTo(76),
            _speedButton.HeightAnchor.ConstraintEqualTo(44)
        ]);
        RadioVaultAccessibility.PrepareView(view);
        ReloadSession();
    }

    private static void ConfigureButton(
        UIButton button,
        UIImage image,
        string accessibilityLabel,
        Action action)
    {
        button.SetImage(image, UIControlState.Normal);
        button.AccessibilityLabel = accessibilityLabel;
        button.TouchUpInside += (_, _) => action();
    }

    private static void ConfigureTimeLabel(UILabel label, UITextAlignment alignment)
    {
        label.Font = RadioVaultAccessibility.ScaledMonospacedDigitFont(12, UIFontWeight.Semibold);
        label.TextAlignment = alignment;
        label.TextColor = RadioVaultTheme.MutedText;
        label.AdjustsFontSizeToFitWidth = true;
        label.MinimumScaleFactor = 0.75f;
    }

    private static void ConfigureActionButton(
        UIButton button,
        string title,
        RadioVaultIcon icon,
        Action action)
    {
        button.SetTitle($" {title}", UIControlState.Normal);
        button.SetTitleColor(RadioVaultTheme.MutedText, UIControlState.Normal);
        button.SetImage(RadioVaultIcons.Image(icon, size: 20), UIControlState.Normal);
        button.TitleLabel!.Font = RadioVaultAccessibility.ScaledFont(12, UIFontWeight.Semibold);
        button.TitleLabel.AdjustsFontForContentSizeCategory = true;
        button.BackgroundColor = RadioVaultTheme.SurfaceRaised;
        button.Layer.CornerRadius = 14;
        button.AccessibilityLabel = title;
        button.TouchUpInside += (_, _) => action();
    }

    private void PresentMomentEditor()
    {
        if (_session.CurrentBroadcast is null) return;
        var alert = UIAlertController.Create(
            "Add Moment",
            "Save this exact point in the broadcast.",
            UIAlertControllerStyle.Alert);
        alert.AddTextField(field => field.Placeholder = "Moment title");
        alert.AddTextField(field => field.Placeholder = "Notes (optional)");
        alert.AddAction(UIAlertAction.Create("Cancel", UIAlertActionStyle.Cancel, null));
        alert.AddAction(UIAlertAction.Create("Save Moment", UIAlertActionStyle.Default, action =>
        {
            var title = alert.TextFields?.ElementAtOrDefault(0)?.Text ?? string.Empty;
            var notes = alert.TextFields?.ElementAtOrDefault(1)?.Text ?? string.Empty;
            BeginMomentSave(title, notes);
        }));
        PresentViewController(alert, true, null);
    }

    private void BeginMomentSave(string title, string notes)
    {
        if (_momentSaving) return;
        _momentSaving = true;
        _momentSavedFeedback = false;
        RefreshSavedActionButtons();
        _ = SaveMomentAsync(title, notes);
    }

    private async Task SaveMomentAsync(string title, string notes)
    {
        var saved = await _session.AddMomentAsync(title, notes).ConfigureAwait(false);
        BeginInvokeOnMainThread(() =>
        {
            _momentSaving = false;
            _momentSavedFeedback = saved;
            if (saved)
            {
                using var feedback = new UINotificationFeedbackGenerator();
                feedback.NotificationOccurred(UINotificationFeedbackType.Success);
            }
            RadioVaultAccessibility.Announce(saved ? "Moment saved" : "Moment could not be saved");
            RefreshSavedActionButtons();
        });
        if (!saved) return;
        await Task.Delay(1400).ConfigureAwait(false);
        BeginInvokeOnMainThread(() =>
        {
            _momentSavedFeedback = false;
            RefreshSavedActionButtons();
        });
    }

    private void OpenBroadcastInformation()
    {
        if (_session.CurrentBroadcast is { } broadcast)
            NavigationController?.PushViewController(new BroadcastDetailsViewController(_session, broadcast), true);
    }

    private void ToggleFavourite()
    {
        if (_favouriteSaving || _session.CurrentBroadcast is not { } broadcast) return;
        _favouriteSaving = true;
        RefreshSavedActionButtons();
        _ = ToggleFavouriteAsync(broadcast, !broadcast.Source.Favourite);
    }

    private async Task ToggleFavouriteAsync(TheRadioVault.Client.Mobile.Models.MobileBroadcastItem broadcast, bool favourite)
    {
        var updated = await _session.SetFavouriteAsync(broadcast, favourite).ConfigureAwait(false);
        BeginInvokeOnMainThread(() =>
        {
            _favouriteSaving = false;
            if (updated is not null)
            {
                using var feedback = new UINotificationFeedbackGenerator();
                feedback.NotificationOccurred(UINotificationFeedbackType.Success);
            }
            RadioVaultAccessibility.Announce(updated is null
                ? "Favourite could not be changed"
                : updated.Source.Favourite ? "Added to favourites" : "Removed from favourites");
            RefreshSavedActionButtons();
        });
    }

    private void ProgressSliderFinished(object? sender, EventArgs eventArgs)
    {
        if (!_isScrubbing) return;
        _isScrubbing = false;
        _session.SeekToProgress(_progressSlider.Value);
    }

    private void SessionOnStateChanged(object? sender, EventArgs eventArgs)
        => BeginInvokeOnMainThread(ReloadSession);

    private void ReloadSession()
    {
        _titleLabel.Text = _session.MiniPlayerTitle;
        _subtitleLabel.Text = _session.MiniPlayerSubtitle;
        _statusLabel.Text = _session.MiniPlayerShowsHandoff
            ? $"Tap the large Radio Vault hand-off button to move playback from {_session.MiniPlayerSubtitle.Replace("Playing on ", string.Empty, StringComparison.Ordinal)} to this iPhone."
            : _session.PlaybackStatus;
        _elapsedLabel.Text = _session.MiniPlayerElapsedTime;
        _totalLabel.Text = _session.MiniPlayerTotalTime;
        _remainingLabel.Text = _session.MiniPlayerRemainingTime;
        _upNext.Configure(_session.QueueItems);
        _progressSlider.AccessibilityValue = _session.MiniPlayerTime;
        if (!_isScrubbing) _progressSlider.Value = (float)_session.MiniPlayerProgress;
        var loading = _session.IsPreparingPlayback;
        if (loading) _playActivity.StartAnimating(); else _playActivity.StopAnimating();
        _playButton.SetImage(loading
            ? null
            : _session.MiniPlayerShowsHandoff
                ? RadioVaultIcons.Image(RadioVaultIcon.Handoff, RadioVaultTheme.Accent, 68, 1.5)
                : RadioVaultIcons.Image(_session.IsPlaying ? RadioVaultIcon.Pause : RadioVaultIcon.Play, RadioVaultTheme.Accent, 68, 1.5f),
            UIControlState.Normal);
        _playButton.TintColor = RadioVaultTheme.Accent;
        _backButton.Enabled = _session.CanSeekPlayback;
        _playButton.Enabled = !loading && _session.MiniPlayerCanAct;
        _playButton.AccessibilityLabel = loading ? "Loading broadcast" : _session.MiniPlayerShowsHandoff
            ? "Move playback to this iPhone"
            : _session.IsPlaying ? "Pause" : "Play";
        _forwardButton.Enabled = _session.CanSeekPlayback;
        _progressSlider.Enabled = _session.CanSeekPlayback;
        _speedButton.Enabled = _session.CanSeekPlayback;
        _speedButton.SetTitle(_session.SpeedText, UIControlState.Normal);
        var broadcast = _session.CurrentBroadcast;
        if (broadcast is not null &&
            (_artworkEpisodeId != broadcast.EpisodeId ||
             (!_artworkWasRequestedOnline && _session.IsLiveConnected)))
        {
            _artworkEpisodeId = broadcast.EpisodeId;
            _artworkWasRequestedOnline = _session.IsLiveConnected;
            RadioVaultArtwork.Load(
                _artworkIcon,
                _session,
                broadcast,
                RadioVaultIcons.Image(RadioVaultIcon.Radio, size: 96, strokeWidth: 1.6f));
        }
        _infoButton.Enabled = broadcast is not null;
        RefreshSavedActionButtons();
    }

    private void RefreshSavedActionButtons()
    {
        var broadcast = _session.CurrentBroadcast;
        _momentButton.Enabled = _session.CanControlPlayback && !_momentSaving;
        _momentButton.SetTitle(
            _momentSaving ? " Saving…" : _momentSavedFeedback ? " Saved" : " Moment",
            UIControlState.Normal);
        _momentButton.SetImage(
            RadioVaultIcons.Image(
                _momentSavedFeedback ? RadioVaultIcon.Completed : RadioVaultIcon.Moment,
                _momentSavedFeedback ? RadioVaultTheme.Completed : RadioVaultTheme.Moment,
                20),
            UIControlState.Normal);
        _momentButton.SetTitleColor(
            _momentSaving || _momentSavedFeedback ? RadioVaultTheme.Moment : RadioVaultTheme.MutedText,
            UIControlState.Normal);
        _momentButton.BackgroundColor = _momentSaving || _momentSavedFeedback
            ? RadioVaultTheme.AccentSubtle
            : RadioVaultTheme.SurfaceRaised;

        _favouriteButton.Enabled = broadcast is not null && !_favouriteSaving;
        var isFavourite = broadcast?.Source.Favourite == true;
        _favouriteButton.SetTitle(
            _favouriteSaving ? " Saving…" : isFavourite ? " Favourited" : " Favourite",
            UIControlState.Normal);
        _favouriteButton.SetImage(
            RadioVaultIcons.Image(RadioVaultIcon.Favourite, RadioVaultTheme.Favourite, 20),
            UIControlState.Normal);
        _favouriteButton.SetTitleColor(
            _favouriteSaving || isFavourite ? RadioVaultTheme.Favourite : RadioVaultTheme.MutedText,
            UIControlState.Normal);
        _favouriteButton.BackgroundColor = _favouriteSaving || isFavourite
            ? RadioVaultTheme.Favourite.ColorWithAlpha(0.14f)
            : RadioVaultTheme.SurfaceRaised;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _session.StateChanged -= SessionOnStateChanged;
            _session.PlaybackStateChanged -= SessionOnStateChanged;
        }
        base.Dispose(disposing);
    }
}
