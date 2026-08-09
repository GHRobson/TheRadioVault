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
        view.BackgroundColor = UIColor.SystemBackground;
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;

        _artworkPanel.BackgroundColor = UIColor.SecondarySystemBackground;
        _artworkPanel.Layer.CornerRadius = 24;
        _artworkPanel.Layer.MasksToBounds = true;
        _artworkIcon.Image = UIImage.GetSystemImage(
            "radio.fill",
            UIImageSymbolConfiguration.Create(82, UIImageSymbolWeight.Regular));
        _artworkIcon.TintColor = UIColor.SystemBlue;
        _artworkIcon.ContentMode = UIViewContentMode.Center;
        _artworkIcon.TranslatesAutoresizingMaskIntoConstraints = false;
        _artworkPanel.AddSubview(_artworkIcon);
        NSLayoutConstraint.ActivateConstraints([
            _artworkIcon.CenterXAnchor.ConstraintEqualTo(_artworkPanel.CenterXAnchor),
            _artworkIcon.CenterYAnchor.ConstraintEqualTo(_artworkPanel.CenterYAnchor)
        ]);

        _titleLabel.Font = (UIFont.PreferredTitle2 ?? UIFont.SystemFontOfSize(24, UIFontWeight.Bold))!;
        _titleLabel.Lines = 0;
        _titleLabel.TextAlignment = UITextAlignment.Center;
        _subtitleLabel.Font = (UIFont.PreferredBody ?? UIFont.SystemFontOfSize(17))!;
        _subtitleLabel.TextColor = UIColor.SecondaryLabel;
        _subtitleLabel.Lines = 0;
        _subtitleLabel.TextAlignment = UITextAlignment.Center;
        _statusLabel.Font = (UIFont.PreferredFootnote ?? UIFont.SystemFontOfSize(13))!;
        _statusLabel.TextColor = UIColor.SecondaryLabel;
        _statusLabel.Lines = 0;
        _statusLabel.TextAlignment = UITextAlignment.Center;
        _timeLabel.Font = UIFont.MonospacedDigitSystemFontOfSize(13, UIFontWeight.Regular)!;
        _timeLabel.TextAlignment = UITextAlignment.Center;
        _timeLabel.TextColor = UIColor.SecondaryLabel;

        _progressSlider.MinValue = 0;
        _progressSlider.MaxValue = 1;
        _progressSlider.Continuous = true;
        _progressSlider.TouchDown += (_, _) => _isScrubbing = true;
        _progressSlider.TouchUpInside += ProgressSliderFinished;
        _progressSlider.TouchUpOutside += ProgressSliderFinished;
        _progressSlider.TouchCancel += ProgressSliderFinished;

        ConfigureButton(_backButton, "gobackward.15", 36, "Back 15 seconds", _session.SkipBack);
        ConfigureButton(_playButton, "play.circle.fill", 68, "Play or pause", _session.MiniPlayerAction);
        ConfigureButton(_forwardButton, "goforward.30", 36, "Forward 30 seconds", _session.SkipForward);
        _playButton.TintColor = UIColor.Label;
        _speedButton.TouchUpInside += (_, _) => _session.CycleSpeed();
        _speedButton.TitleLabel!.Font = UIFont.SystemFontOfSize(16, UIFontWeight.Semibold)!;
        _speedButton.BackgroundColor = UIColor.TertiarySystemFill;
        _speedButton.Layer.CornerRadius = 19;
        _speedButton.AccessibilityLabel = "Playback speed";

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
        var content = new UIStackView([_artworkPanel, metadata, progress, controls, _speedButton, _statusLabel])
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
            _speedButton.WidthAnchor.ConstraintEqualTo(76),
            _speedButton.HeightAnchor.ConstraintEqualTo(38)
        ]);
        ReloadSession();
    }

    private static void ConfigureButton(
        UIButton button,
        string symbol,
        int pointSize,
        string accessibilityLabel,
        Action action)
    {
        button.SetImage(UIImage.GetSystemImage(symbol), UIControlState.Normal);
        button.SetPreferredSymbolConfiguration(
            UIImageSymbolConfiguration.Create(pointSize, UIImageSymbolWeight.Semibold),
            UIControlState.Normal);
        button.AccessibilityLabel = accessibilityLabel;
        button.TouchUpInside += (_, _) => action();
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
            ? $"Tap the large AirPlay button to move playback from {_session.MiniPlayerSubtitle.Replace("Playing on ", string.Empty, StringComparison.Ordinal)} to this iPhone."
            : _session.PlaybackStatus;
        _timeLabel.Text = _session.PlaybackTime;
        if (!_isScrubbing) _progressSlider.Value = (float)_session.PlaybackProgress;
        _playButton.SetImage(UIImage.GetSystemImage(
            _session.MiniPlayerShowsHandoff ? "airplayaudio.circle.fill" :
            _session.IsPlaying ? "pause.circle.fill" : "play.circle.fill"), UIControlState.Normal);
        _backButton.Enabled = _session.CanControlPlayback;
        _playButton.Enabled = _session.MiniPlayerCanAct;
        _forwardButton.Enabled = _session.CanControlPlayback;
        _progressSlider.Enabled = _session.CanControlPlayback;
        _speedButton.Enabled = _session.CanControlPlayback;
        _speedButton.SetTitle(_session.SpeedText, UIControlState.Normal);
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
