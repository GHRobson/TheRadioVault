using TheRadioVault.Client.Mobile;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class RadioVaultMiniPlayerView : UIView
{
    private readonly MobileClientSession _session;
    private readonly UIImageView _artwork = new();
    private readonly UILabel _titleLabel = new();
    private readonly UILabel _subtitleLabel = new();
    private readonly UIButton _actionButton = UIButton.FromType(UIButtonType.System);
    private readonly UIActivityIndicatorView _activity = new(UIActivityIndicatorViewStyle.Medium);
    private readonly UIProgressView _progress = new(UIProgressViewStyle.Default);
    private readonly UIVisualEffectView _glassBackground;
    private long _artworkEpisodeId;
    private bool _artworkWasRequestedOnline;

    public RadioVaultMiniPlayerView(MobileClientSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _glassBackground = new UIVisualEffectView(CreateBackgroundEffect())
        {
            TranslatesAutoresizingMaskIntoConstraints = false,
            UserInteractionEnabled = false
        };
        _glassBackground.ContentView.BackgroundColor = RadioVaultTheme.Shell.ColorWithAlpha(0.16f);
        TranslatesAutoresizingMaskIntoConstraints = false;
        BackgroundColor = UIColor.Clear;
        Layer.BorderColor = RadioVaultTheme.Border.CGColor;
        Layer.BorderWidth = 1;
        Layer.CornerRadius = 24;
        Layer.MasksToBounds = true;
        AccessibilityTraits = UIAccessibilityTrait.Button;

        _titleLabel.Font = (UIFont.PreferredSubheadline ?? UIFont.SystemFontOfSize(15, UIFontWeight.Semibold))!;
        _titleLabel.TextColor = RadioVaultTheme.Text;
        _titleLabel.Lines = 1;
        _titleLabel.LineBreakMode = UILineBreakMode.TailTruncation;
        _subtitleLabel.Font = (UIFont.PreferredCaption1 ?? UIFont.SystemFontOfSize(12))!;
        _subtitleLabel.TextColor = RadioVaultTheme.MutedText;
        _subtitleLabel.Lines = 1;
        _subtitleLabel.LineBreakMode = UILineBreakMode.TailTruncation;
        _actionButton.TouchUpInside += (_, _) => _session.MiniPlayerAction();
        _activity.Color = RadioVaultTheme.Accent;
        _activity.HidesWhenStopped = true;
        _activity.TranslatesAutoresizingMaskIntoConstraints = false;
        _actionButton.AddSubview(_activity);
        _artwork.BackgroundColor = RadioVaultTheme.AccentSubtle;
        _artwork.Layer.CornerRadius = 10;
        _artwork.Layer.MasksToBounds = true;

        var labels = new UIStackView([_titleLabel, _subtitleLabel])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 1
        };
        labels.UserInteractionEnabled = true;
        var row = new UIStackView([_artwork, labels, _actionButton])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _progress.TranslatesAutoresizingMaskIntoConstraints = false;
        _progress.ProgressTintColor = RadioVaultTheme.Accent;
        _progress.TrackTintColor = RadioVaultTheme.Border.ColorWithAlpha(0.72f);
        AddSubview(_glassBackground);
        AddSubview(row);
        AddSubview(_progress);
        NSLayoutConstraint.ActivateConstraints([
            _glassBackground.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _glassBackground.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _glassBackground.TopAnchor.ConstraintEqualTo(TopAnchor),
            _glassBackground.BottomAnchor.ConstraintEqualTo(BottomAnchor),
            row.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 8),
            row.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -10),
            row.TopAnchor.ConstraintEqualTo(TopAnchor, 7),
            row.BottomAnchor.ConstraintEqualTo(_progress.TopAnchor, -6),
            _actionButton.WidthAnchor.ConstraintEqualTo(44),
            _actionButton.HeightAnchor.ConstraintEqualTo(44),
            _artwork.WidthAnchor.ConstraintEqualTo(42),
            _artwork.HeightAnchor.ConstraintEqualTo(42),
            _activity.CenterXAnchor.ConstraintEqualTo(_actionButton.CenterXAnchor),
            _activity.CenterYAnchor.ConstraintEqualTo(_actionButton.CenterYAnchor),
            _progress.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _progress.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _progress.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);

        labels.AddGestureRecognizer(new UITapGestureRecognizer(() => Tapped?.Invoke(this, EventArgs.Empty)));
        _session.StateChanged += SessionOnStateChanged;
        _session.PlaybackStateChanged += SessionOnStateChanged;
        RadioVaultAccessibility.PrepareView(this);
        Reload();
    }

    private static UIVisualEffect CreateBackgroundEffect()
        => UIBlurEffect.FromStyle(UIBlurEffectStyle.SystemChromeMaterialDark);

    public event EventHandler? Tapped;

    private void SessionOnStateChanged(object? sender, EventArgs eventArgs)
        => BeginInvokeOnMainThread(Reload);

    private void Reload()
    {
        Hidden = !_session.HasMiniPlayer;
        _titleLabel.Text = _session.MiniPlayerTitle;
        _subtitleLabel.Text = _session.MiniPlayerSubtitle;
        _progress.Progress = (float)_session.MiniPlayerProgress;
        if (_session.CurrentBroadcast is { } broadcast &&
            (_artworkEpisodeId != broadcast.EpisodeId ||
             (!_artworkWasRequestedOnline && _session.IsLiveConnected)))
        {
            _artworkEpisodeId = broadcast.EpisodeId;
            _artworkWasRequestedOnline = _session.IsLiveConnected;
            RadioVaultArtwork.Load(_artwork, _session, broadcast);
        }
        var loading = _session.IsPreparingPlayback || _session.IsLiveRadioLoading;
        if (loading) _activity.StartAnimating(); else _activity.StopAnimating();
        _actionButton.SetImage(loading
            ? null
            : _session.MiniPlayerShowsHandoff
                ? RadioVaultIcons.Image(RadioVaultIcon.Handoff, RadioVaultTheme.Accent, 30, 2.5)
                : RadioVaultIcons.Image(
                    _session.IsLiveRadioTunedIn || _session.IsPlaying ? RadioVaultIcon.Pause : RadioVaultIcon.Play,
                    RadioVaultTheme.Accent,
                    30,
                    2.5), UIControlState.Normal);
        _actionButton.TintColor = RadioVaultTheme.Accent;
        _actionButton.Enabled = !loading && _session.MiniPlayerCanAct;
        _actionButton.AccessibilityLabel = loading
            ? "Loading broadcast"
            : _session.IsLiveRadioTunedIn ? "Leave Radio Vault Live"
            : _session.MiniPlayerShowsHandoff
            ? "Move playback to this iPhone"
            : _session.IsPlaying ? "Pause" : "Play";
        AccessibilityLabel = $"Now Playing, {_session.MiniPlayerTitle}, {_session.MiniPlayerSubtitle}";
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
