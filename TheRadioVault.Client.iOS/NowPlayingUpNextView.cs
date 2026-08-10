using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Web.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

internal sealed class NowPlayingUpNextView : UIView
{
    private readonly MobileClientSession _session;
    private readonly UILabel _count = new();
    private readonly UIStackView _items = new()
    {
        Axis = UILayoutConstraintAxis.Vertical,
        Alignment = UIStackViewAlignment.Fill,
        Spacing = 10
    };
    private string _signature = string.Empty;

    public NowPlayingUpNextView(MobileClientSession session)
    {
        _session = session;
        var rule = new UIView { BackgroundColor = RadioVaultTheme.Border };
        var eyebrow = new UILabel
        {
            Text = "SHARED QUEUE",
            Font = UIFont.SystemFontOfSize(10, UIFontWeight.Bold)!,
            TextColor = RadioVaultTheme.Accent
        };
        var title = new UILabel
        {
            Text = "Up Next",
            Font = (UIFont.PreferredTitle2 ?? UIFont.SystemFontOfSize(24, UIFontWeight.Bold))!,
            TextColor = RadioVaultTheme.Text,
            AdjustsFontForContentSizeCategory = true
        };
        _count.Font = (UIFont.PreferredCaption1 ?? UIFont.SystemFontOfSize(12))!;
        _count.TextColor = RadioVaultTheme.MutedText;
        _count.TextAlignment = UITextAlignment.Right;
        var heading = new UIStackView([title, _count])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.LastBaseline,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 10
        };
        var explanation = new UILabel
        {
            Text = "This queue is shared with every Radio Vault player. Tap a broadcast to play it now.",
            Font = (UIFont.PreferredFootnote ?? UIFont.SystemFontOfSize(13))!,
            TextColor = RadioVaultTheme.MutedText,
            Lines = 0,
            AdjustsFontForContentSizeCategory = true
        };
        var content = new UIStackView([rule, eyebrow, heading, explanation, _items])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 10,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        AddSubview(content);
        NSLayoutConstraint.ActivateConstraints([
            rule.HeightAnchor.ConstraintEqualTo(1),
            content.LeadingAnchor.ConstraintEqualTo(LeadingAnchor),
            content.TrailingAnchor.ConstraintEqualTo(TrailingAnchor),
            content.TopAnchor.ConstraintEqualTo(TopAnchor, 18),
            content.BottomAnchor.ConstraintEqualTo(BottomAnchor)
        ]);
    }

    public void Configure(IReadOnlyList<WebQueueItem> queue)
    {
        var signature = string.Join('|', queue.Select(value =>
            $"{value.QueueId}:{value.Position}:{value.Episode.PositionMs}:{value.Episode.Status}"));
        _count.Text = queue.Count == 0 ? "Empty" : $"{queue.Count:N0} queued";
        if (signature == _signature) return;
        _signature = signature;
        foreach (var view in _items.ArrangedSubviews) view.RemoveFromSuperview();
        if (queue.Count == 0)
        {
            _items.AddArrangedSubview(new UILabel
            {
                Text = "Nothing is queued. Press and hold a broadcast anywhere in the app and choose Play Next.",
                Font = (UIFont.PreferredBody ?? UIFont.SystemFontOfSize(16))!,
                TextColor = RadioVaultTheme.MutedText,
                Lines = 0,
                TextAlignment = UITextAlignment.Center,
                AdjustsFontForContentSizeCategory = true
            });
            return;
        }
        foreach (var item in queue.OrderBy(value => value.Position))
            _items.AddArrangedSubview(new NowPlayingQueueItemView(_session, item));
    }
}

internal sealed class NowPlayingQueueItemView : UIControl
{
    public NowPlayingQueueItemView(MobileClientSession session, WebQueueItem item)
    {
        BackgroundColor = RadioVaultTheme.SurfaceRaised;
        Layer.CornerRadius = 14;
        Layer.BorderColor = RadioVaultTheme.Border.CGColor;
        Layer.BorderWidth = 1;
        AccessibilityTraits = UIAccessibilityTrait.Button;
        AccessibilityLabel = $"{item.Position + 1}, {item.Episode.Title}, {item.Episode.Show}, {item.Episode.ProgressPercent} percent listened";
        TouchUpInside += (_, _) => _ = session.PlayQueueItemAsync(item);

        var artwork = new UIImageView
        {
            BackgroundColor = RadioVaultTheme.AccentSubtle,
            ContentMode = UIViewContentMode.ScaleAspectFill,
            ClipsToBounds = true
        };
        artwork.Layer.CornerRadius = 10;
        RadioVaultArtwork.Load(artwork, session, item.Episode.Id);
        var number = new UILabel
        {
            Text = (item.Position + 1).ToString(),
            Font = UIFont.MonospacedDigitSystemFontOfSize(11, UIFontWeight.Bold)!,
            TextColor = RadioVaultTheme.Accent,
            TextAlignment = UITextAlignment.Center
        };
        var title = new UILabel
        {
            Text = item.Episode.Title,
            Font = UIFont.SystemFontOfSize(15, UIFontWeight.Semibold)!,
            TextColor = RadioVaultTheme.Text,
            Lines = 2
        };
        var detail = new UILabel
        {
            Text = $"{item.Episode.Show} · {item.Episode.Status}",
            Font = UIFont.SystemFontOfSize(11)!,
            TextColor = RadioVaultTheme.MutedText,
            Lines = 2
        };
        var progress = new UIProgressView(UIProgressViewStyle.Default)
        {
            Progress = item.Episode.ProgressPercent / 100f,
            ProgressTintColor = RadioVaultTheme.Accent,
            TrackTintColor = RadioVaultTheme.Border
        };
        var labels = new UIStackView([title, detail, progress])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 5
        };
        var remove = UIButton.FromType(UIButtonType.System);
        remove.SetImage(RadioVaultIcons.Image(RadioVaultIcon.Remove, RadioVaultTheme.MutedText, 20, 2), UIControlState.Normal);
        remove.AccessibilityLabel = $"Remove {item.Episode.Title} from Up Next";
        remove.TouchUpInside += (_, _) => _ = session.RemoveQueueItemAsync(item);
        var content = new UIStackView([number, artwork, labels, remove])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 10,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        AddSubview(content);
        NSLayoutConstraint.ActivateConstraints([
            number.WidthAnchor.ConstraintEqualTo(20),
            artwork.WidthAnchor.ConstraintEqualTo(52),
            artwork.HeightAnchor.ConstraintEqualTo(52),
            remove.WidthAnchor.ConstraintEqualTo(36),
            remove.HeightAnchor.ConstraintEqualTo(44),
            content.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 10),
            content.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -8),
            content.TopAnchor.ConstraintEqualTo(TopAnchor, 10),
            content.BottomAnchor.ConstraintEqualTo(BottomAnchor, -10)
        ]);
    }
}
