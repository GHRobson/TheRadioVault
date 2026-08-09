using TheRadioVault.Client.Mobile;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class NowPlayingViewController : UIViewController
{
    private readonly MobileClientSession _session;
    private readonly UILabel _titleLabel = new();
    private readonly UILabel _subtitleLabel = new();
    private readonly UILabel _statusLabel = new();
    private readonly UILabel _timeLabel = new();
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
    private bool _isScrubbing;

    public NowPlayingViewController(MobileClientSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
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
        NavigationItem.LeftBarButtonItem = new UIBarButtonItem(
            RadioVaultIcons.Image(RadioVaultIcon.UpNext),
            UIBarButtonItemStyle.Plain,
            (_, _) => NavigationController?.PushViewController(new UpNextViewController(_session), true));
        NavigationItem.LeftBarButtonItem.AccessibilityLabel = "Up Next";

        _artworkPanel.BackgroundColor = RadioVaultTheme.Surface;
        _artworkPanel.Layer.CornerRadius = 24;
        _artworkPanel.Layer.MasksToBounds = true;
        _artworkIcon.Image = RadioVaultIcons.Image(RadioVaultIcon.Radio, size: 96, strokeWidth: 1.6f);
        _artworkIcon.ContentMode = UIViewContentMode.Center;
        _artworkIcon.TranslatesAutoresizingMaskIntoConstraints = false;
        _artworkPanel.AddSubview(_artworkIcon);
        NSLayoutConstraint.ActivateConstraints([
            _artworkIcon.CenterXAnchor.ConstraintEqualTo(_artworkPanel.CenterXAnchor),
            _artworkIcon.CenterYAnchor.ConstraintEqualTo(_artworkPanel.CenterYAnchor)
        ]);

        _titleLabel.Font = (UIFont.PreferredTitle2 ?? UIFont.SystemFontOfSize(24, UIFontWeight.Bold))!;
        _titleLabel.TextColor = RadioVaultTheme.Text;
        _titleLabel.Lines = 0;
        _titleLabel.TextAlignment = UITextAlignment.Center;
        _subtitleLabel.Font = (UIFont.PreferredBody ?? UIFont.SystemFontOfSize(17))!;
        _subtitleLabel.TextColor = RadioVaultTheme.MutedText;
        _subtitleLabel.Lines = 0;
        _subtitleLabel.TextAlignment = UITextAlignment.Center;
        _statusLabel.Font = (UIFont.PreferredFootnote ?? UIFont.SystemFontOfSize(13))!;
        _statusLabel.TextColor = RadioVaultTheme.MutedText;
        _statusLabel.Lines = 0;
        _statusLabel.TextAlignment = UITextAlignment.Center;
        _timeLabel.Font = UIFont.MonospacedDigitSystemFontOfSize(13, UIFontWeight.Regular)!;
        _timeLabel.TextAlignment = UITextAlignment.Center;
        _timeLabel.TextColor = RadioVaultTheme.MutedText;

        _progressSlider.MinValue = 0;
        _progressSlider.MaxValue = 1;
        _progressSlider.Continuous = true;
        _progressSlider.TouchDown += (_, _) => _isScrubbing = true;
        _progressSlider.TouchUpInside += ProgressSliderFinished;
        _progressSlider.TouchUpOutside += ProgressSliderFinished;
        _progressSlider.TouchCancel += ProgressSliderFinished;

        ConfigureButton(_backButton, RadioVaultIcons.Image(RadioVaultIcon.SkipBack, RadioVaultTheme.Accent, 42), "Back 15 seconds", _session.SkipBack);
        ConfigureButton(_playButton, RadioVaultIcons.Image(RadioVaultIcon.Play, RadioVaultTheme.Accent, 68, 1.5f), "Play or pause", _session.MiniPlayerAction);
        ConfigureButton(_forwardButton, RadioVaultIcons.Image(RadioVaultIcon.SkipForward, RadioVaultTheme.Accent, 42), "Forward 30 seconds", _session.SkipForward);
        _backButton.TintColor = RadioVaultTheme.Accent;
        _forwardButton.TintColor = RadioVaultTheme.Accent;
        _speedButton.TouchUpInside += (_, _) => _session.CycleSpeed();
        _speedButton.TitleLabel!.Font = UIFont.SystemFontOfSize(16, UIFontWeight.Semibold)!;
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
            Distribution = UIStackViewDistribution.EqualSpacing,
            Spacing = 24
        };
        var metadata = new UIStackView([_titleLabel, _subtitleLabel])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 6
        };
        var progress = new UIStackView([_progressSlider, _timeLabel])
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
        var content = new UIStackView([_artworkPanel, metadata, progress, controls, actions, _speedButton, _statusLabel])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 20,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        content.SetCustomSpacing(28, _artworkPanel);
        content.SetCustomSpacing(26, progress);
        content.SetCustomSpacing(16, controls);

        view.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints([
            content.LeadingAnchor.ConstraintEqualTo(view.SafeAreaLayoutGuide.LeadingAnchor, 28),
            content.TrailingAnchor.ConstraintEqualTo(view.SafeAreaLayoutGuide.TrailingAnchor, -28),
            content.TopAnchor.ConstraintGreaterThanOrEqualTo(view.SafeAreaLayoutGuide.TopAnchor, 12),
            content.BottomAnchor.ConstraintLessThanOrEqualTo(view.SafeAreaLayoutGuide.BottomAnchor, -20),
            content.CenterYAnchor.ConstraintEqualTo(view.SafeAreaLayoutGuide.CenterYAnchor),
            _artworkPanel.HeightAnchor.ConstraintEqualTo(210),
            _backButton.WidthAnchor.ConstraintEqualTo(64),
            _backButton.HeightAnchor.ConstraintEqualTo(64),
            _playButton.WidthAnchor.ConstraintEqualTo(96),
            _playButton.HeightAnchor.ConstraintEqualTo(96),
            _forwardButton.WidthAnchor.ConstraintEqualTo(64),
            _forwardButton.HeightAnchor.ConstraintEqualTo(64),
            actions.HeightAnchor.ConstraintEqualTo(48),
            _speedButton.WidthAnchor.ConstraintEqualTo(76),
            _speedButton.HeightAnchor.ConstraintEqualTo(38)
        ]);
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

    private static void ConfigureActionButton(
        UIButton button,
        string title,
        RadioVaultIcon icon,
        Action action)
    {
        button.SetTitle($" {title}", UIControlState.Normal);
        button.SetTitleColor(RadioVaultTheme.MutedText, UIControlState.Normal);
        button.SetImage(RadioVaultIcons.Image(icon, size: 20), UIControlState.Normal);
        button.TitleLabel!.Font = UIFont.SystemFontOfSize(12, UIFontWeight.Semibold)!;
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
            _ = _session.AddMomentAsync(title, notes);
        }));
        PresentViewController(alert, true, null);
    }

    private void OpenBroadcastInformation()
    {
        if (_session.CurrentBroadcast is { } broadcast)
            NavigationController?.PushViewController(new BroadcastDetailsViewController(_session, broadcast), true);
    }

    private void ToggleFavourite()
    {
        if (_session.CurrentBroadcast is { } broadcast)
            _ = _session.SetFavouriteAsync(broadcast, !broadcast.Source.Favourite);
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
        _timeLabel.Text = _session.PlaybackTime;
        if (!_isScrubbing) _progressSlider.Value = (float)_session.PlaybackProgress;
        _playButton.SetImage(
            _session.MiniPlayerShowsHandoff
                ? RadioVaultIcons.Image(RadioVaultIcon.Handoff, RadioVaultTheme.Accent, 68, 1.5)
                : RadioVaultIcons.Image(_session.IsPlaying ? RadioVaultIcon.Pause : RadioVaultIcon.Play, RadioVaultTheme.Accent, 68, 1.5f),
            UIControlState.Normal);
        _playButton.TintColor = RadioVaultTheme.Accent;
        _backButton.Enabled = _session.CanControlPlayback;
        _playButton.Enabled = _session.MiniPlayerCanAct;
        _forwardButton.Enabled = _session.CanControlPlayback;
        _progressSlider.Enabled = _session.CanControlPlayback;
        _speedButton.Enabled = _session.CanControlPlayback;
        _speedButton.SetTitle(_session.SpeedText, UIControlState.Normal);
        var broadcast = _session.CurrentBroadcast;
        _momentButton.Enabled = _session.CanControlPlayback && _session.IsLiveConnected;
        _infoButton.Enabled = broadcast is not null;
        _favouriteButton.Enabled = broadcast is not null && _session.IsLiveConnected;
        _favouriteButton.SetTitle(
            broadcast?.Source.Favourite == true ? " Favourited" : " Favourite",
            UIControlState.Normal);
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
