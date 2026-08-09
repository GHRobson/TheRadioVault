using TheRadioVault.Client.Mobile;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class RadioVaultMiniPlayerView : UIView
{
    private readonly MobileClientSession _session;
    private readonly UILabel _titleLabel = new();
    private readonly UILabel _subtitleLabel = new();
    private readonly UIButton _actionButton = UIButton.FromType(UIButtonType.System);
    private readonly UIProgressView _progress = new(UIProgressViewStyle.Default);

    public RadioVaultMiniPlayerView(MobileClientSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        TranslatesAutoresizingMaskIntoConstraints = false;
        BackgroundColor = RadioVaultTheme.SurfaceRaised;
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

        var labels = new UIStackView([_titleLabel, _subtitleLabel])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 1
        };
        labels.UserInteractionEnabled = true;
        var row = new UIStackView([labels, _actionButton])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _progress.TranslatesAutoresizingMaskIntoConstraints = false;
        AddSubview(row);
        AddSubview(_progress);
        NSLayoutConstraint.ActivateConstraints([
            row.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 14),
            row.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -10),
            row.TopAnchor.ConstraintEqualTo(TopAnchor, 7),
            row.BottomAnchor.ConstraintEqualTo(_progress.TopAnchor, -6),
            _actionButton.WidthAnchor.ConstraintEqualTo(44),
            _actionButton.HeightAnchor.ConstraintEqualTo(44),
            _progress.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            _progress.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            _progress.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);

        labels.AddGestureRecognizer(new UITapGestureRecognizer(() => Tapped?.Invoke(this, EventArgs.Empty)));
        _session.StateChanged += SessionOnStateChanged;
        _session.PlaybackStateChanged += SessionOnStateChanged;
        Reload();
    }

    public event EventHandler? Tapped;

    private void SessionOnStateChanged(object? sender, EventArgs eventArgs)
        => BeginInvokeOnMainThread(Reload);

    private void Reload()
    {
        Hidden = !_session.HasMiniPlayer;
        _titleLabel.Text = _session.MiniPlayerTitle;
        _subtitleLabel.Text = _session.MiniPlayerSubtitle;
        _progress.Progress = (float)_session.MiniPlayerProgress;
        _actionButton.SetImage(
            _session.MiniPlayerShowsHandoff
                ? RadioVaultIcons.Image(RadioVaultIcon.Handoff)
                : RadioVaultIcons.Image(_session.IsPlaying ? RadioVaultIcon.Pause : RadioVaultIcon.Play),
            UIControlState.Normal);
        _actionButton.TintColor = RadioVaultTheme.Progress;
        _actionButton.Enabled = _session.MiniPlayerCanAct;
        _actionButton.AccessibilityLabel = _session.MiniPlayerShowsHandoff
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
