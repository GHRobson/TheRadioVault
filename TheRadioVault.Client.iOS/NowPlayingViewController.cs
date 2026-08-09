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
    private readonly UIProgressView _progress = new(UIProgressViewStyle.Default);
    private readonly UIButton _backButton = UIButton.FromType(UIButtonType.System);
    private readonly UIButton _playButton = UIButton.FromType(UIButtonType.System);
    private readonly UIButton _forwardButton = UIButton.FromType(UIButtonType.System);
    private readonly UIButton _speedButton = UIButton.FromType(UIButtonType.System);

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

        _titleLabel.Font = (UIFont.PreferredTitle1 ?? UIFont.SystemFontOfSize(28, UIFontWeight.Bold))!;
        _titleLabel.Lines = 0;
        _titleLabel.TextAlignment = UITextAlignment.Center;
        _subtitleLabel.Font = (UIFont.PreferredSubheadline ?? UIFont.SystemFontOfSize(15))!;
        _subtitleLabel.TextColor = UIColor.SecondaryLabel;
        _subtitleLabel.Lines = 0;
        _subtitleLabel.TextAlignment = UITextAlignment.Center;
        _statusLabel.Font = (UIFont.PreferredFootnote ?? UIFont.SystemFontOfSize(13))!;
        _statusLabel.TextColor = UIColor.SecondaryLabel;
        _statusLabel.Lines = 0;
        _statusLabel.TextAlignment = UITextAlignment.Center;
        _timeLabel.Font = UIFont.MonospacedDigitSystemFontOfSize(13, UIFontWeight.Regular)!;
        _timeLabel.TextAlignment = UITextAlignment.Center;

        ConfigureButton(_backButton, "gobackward.15", "Back 15 seconds", _session.SkipBack);
        ConfigureButton(_playButton, "play.circle.fill", "Play or pause", _session.MiniPlayerAction);
        ConfigureButton(_forwardButton, "goforward.30", "Forward 30 seconds", _session.SkipForward);
        _playButton.ImageView!.ContentMode = UIViewContentMode.ScaleAspectFit;
        _speedButton.TouchUpInside += (_, _) => _session.CycleSpeed();

        var controls = new UIStackView([_backButton, _playButton, _forwardButton])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Distribution = UIStackViewDistribution.EqualSpacing,
            Spacing = 28
        };
        var content = new UIStackView([_titleLabel, _subtitleLabel, _progress, _timeLabel, controls, _speedButton, _statusLabel])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 18,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        content.SetCustomSpacing(40, _subtitleLabel);
        view.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints([
            content.LeadingAnchor.ConstraintEqualTo(view.SafeAreaLayoutGuide.LeadingAnchor, 28),
            content.TrailingAnchor.ConstraintEqualTo(view.SafeAreaLayoutGuide.TrailingAnchor, -28),
            content.CenterYAnchor.ConstraintEqualTo(view.SafeAreaLayoutGuide.CenterYAnchor),
            _playButton.WidthAnchor.ConstraintEqualTo(72),
            _playButton.HeightAnchor.ConstraintEqualTo(72)
        ]);
        ReloadSession();
    }

    private static void ConfigureButton(UIButton button, string symbol, string accessibilityLabel, Action action)
    {
        button.SetImage(UIImage.GetSystemImage(symbol), UIControlState.Normal);
        button.AccessibilityLabel = accessibilityLabel;
        button.TouchUpInside += (_, _) => action();
    }

    private void SessionOnStateChanged(object? sender, EventArgs eventArgs)
        => BeginInvokeOnMainThread(ReloadSession);

    private void ReloadSession()
    {
        _titleLabel.Text = _session.MiniPlayerTitle;
        _subtitleLabel.Text = _session.MiniPlayerSubtitle;
        _statusLabel.Text = _session.MiniPlayerShowsHandoff
            ? $"Move playback from {_session.MiniPlayerSubtitle.Replace("Playing on ", string.Empty, StringComparison.Ordinal)}"
            : _session.PlaybackStatus;
        _timeLabel.Text = _session.PlaybackTime;
        _progress.Progress = (float)_session.PlaybackProgress;
        _playButton.SetImage(UIImage.GetSystemImage(
            _session.MiniPlayerShowsHandoff ? "airplayaudio.circle.fill" :
            _session.IsPlaying ? "pause.circle.fill" : "play.circle.fill"), UIControlState.Normal);
        _backButton.Enabled = _session.CanControlPlayback;
        _playButton.Enabled = _session.MiniPlayerCanAct;
        _forwardButton.Enabled = _session.CanControlPlayback;
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
