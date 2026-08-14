using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Web.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

internal sealed class DashboardOnThisDayCarouselCell : UITableViewCell
{
    private readonly UIControl _card = new();
    private readonly UIImageView _artwork = new();
    private readonly UILabel _title = new();
    private readonly UILabel _show = new();
    private readonly UILabel _date = new();
    private readonly UILabel _peopleHeading = SectionHeading("PEOPLE");
    private readonly UILabel _topicsHeading = SectionHeading("TOPICS");
    private readonly UIStackView _people = PillRow();
    private readonly UIStackView _topics = PillRow();
    private readonly UIStackView _pips = new()
    {
        Axis = UILayoutConstraintAxis.Horizontal,
        Alignment = UIStackViewAlignment.Center,
        Distribution = UIStackViewDistribution.EqualSpacing,
        Spacing = 6
    };
    private readonly UIButton _play = UIButton.FromType(UIButtonType.System);
    private IReadOnlyList<MobileBroadcastItem> _items = [];
    private Func<MobileBroadcastItem, Task<WebClientBroadcastDetails?>>? _loadDetails;
    private Action<MobileBroadcastItem>? _openBroadcast;
    private Action<string>? _openEntity;
    private MobileClientSession? _session;
    private NSTimer? _timer;
    private int _index;
    private int _generation;

    public DashboardOnThisDayCarouselCell() : base(UITableViewCellStyle.Default, "dashboard-on-this-day-carousel")
    {
        BackgroundColor = RadioVaultTheme.Background;
        ContentView.BackgroundColor = RadioVaultTheme.Background;
        SelectionStyle = UITableViewCellSelectionStyle.None;
        _card.BackgroundColor = RadioVaultTheme.SurfaceRaised;
        _card.Layer.CornerRadius = 18;
        _card.Layer.BorderWidth = 1;
        _card.Layer.BorderColor = RadioVaultTheme.Border.CGColor;
        _card.TranslatesAutoresizingMaskIntoConstraints = false;
        _card.TouchUpInside += (_, _) => OpenCurrent();

        _artwork.ContentMode = UIViewContentMode.ScaleAspectFill;
        _artwork.ClipsToBounds = true;
        _artwork.Layer.CornerRadius = 13;
        _artwork.BackgroundColor = RadioVaultTheme.AccentSubtle;
        _artwork.TranslatesAutoresizingMaskIntoConstraints = false;
        _title.Font = RadioVaultAccessibility.ScaledFont(18, UIFontWeight.Bold);
        _title.TextColor = RadioVaultTheme.Text;
        _title.Lines = 2;
        _show.Font = RadioVaultAccessibility.ScaledFont(12, UIFontWeight.Semibold);
        _show.TextColor = RadioVaultTheme.Accent;
        _date.Font = RadioVaultAccessibility.ScaledFont(11);
        _date.TextColor = RadioVaultTheme.MutedText;

        _play.SetImage(RadioVaultIcons.Image(RadioVaultIcon.Play, RadioVaultTheme.Accent, 22, 2.3), UIControlState.Normal);
        _play.BackgroundColor = RadioVaultTheme.AccentSubtle;
        _play.Layer.CornerRadius = 19;
        _play.AccessibilityLabel = "Play this broadcast";
        _play.TouchUpInside += (_, _) => PlayCurrent();
        _play.WidthAnchor.ConstraintEqualTo(38).Active = true;
        _play.HeightAnchor.ConstraintEqualTo(38).Active = true;

        var titleBlock = new UIStackView([_title, _show, _date])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 4
        };
        var titleAndPlay = new UIStackView([titleBlock, _play])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 8
        };
        var main = new UIStackView([_artwork, titleAndPlay])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 13
        };
        var content = new UIStackView([main, _peopleHeading, _people, _topicsHeading, _topics, _pips])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 7,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _card.AddSubview(content);
        ContentView.AddSubview(_card);
        NSLayoutConstraint.ActivateConstraints([
            _card.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 8),
            _card.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -8),
            _card.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 4),
            _card.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -8),
            content.LeadingAnchor.ConstraintEqualTo(_card.LeadingAnchor, 14),
            content.TrailingAnchor.ConstraintEqualTo(_card.TrailingAnchor, -14),
            content.TopAnchor.ConstraintEqualTo(_card.TopAnchor, 14),
            content.BottomAnchor.ConstraintEqualTo(_card.BottomAnchor, -12),
            _artwork.WidthAnchor.ConstraintEqualTo(92),
            _artwork.HeightAnchor.ConstraintEqualTo(92),
            _pips.HeightAnchor.ConstraintEqualTo(12)
        ]);
    }

    public void Configure(
        MobileClientSession session,
        IReadOnlyList<MobileBroadcastItem> items,
        Func<MobileBroadcastItem, Task<WebClientBroadcastDetails?>> loadDetails,
        Action<MobileBroadcastItem> openBroadcast,
        Action<string> openEntity)
    {
        _session = session;
        _items = items;
        _loadDetails = loadDetails;
        _openBroadcast = openBroadcast;
        _openEntity = openEntity;
        _index = Math.Clamp(_index, 0, Math.Max(0, items.Count - 1));
        RenderCurrent(animated: false);
        StartTimer();
    }

    private void Advance()
    {
        if (_items.Count < 2) return;
        _index = (_index + 1) % _items.Count;
        RenderCurrent(animated: true);
    }

    private void RenderCurrent(bool animated)
    {
        if (_session is null || _items.Count == 0) return;
        var item = _items[_index];
        void Apply()
        {
            RadioVaultArtwork.Load(_artwork, _session, item, RadioVaultIcons.Image(RadioVaultIcon.Radio, size: 44));
            _title.Text = item.Title;
            _show.Text = item.Source.CollectionName;
            _date.Text = item.Source.AirDate?.ToString("dddd, d MMMM yyyy") ?? "Date unknown";
            ConfigurePlaceholder(_people, "Loading people…", RadioVaultTheme.ActivityBlue);
            ConfigurePlaceholder(_topics, "Loading topics…", RadioVaultTheme.Research);
            ConfigurePips();
            _card.AccessibilityLabel = $"On this day, {item.Title}, {item.Source.CollectionName}, item {_index + 1} of {_items.Count}";
        }
        if (animated && !RadioVaultAccessibility.ReduceMotion)
            UIView.Transition(_card, 0.28, UIViewAnimationOptions.TransitionCrossDissolve, Apply, () => { });
        else Apply();
        _ = LoadMetadataAsync(item, ++_generation);
    }

    private async Task LoadMetadataAsync(MobileBroadcastItem item, int generation)
    {
        if (_loadDetails is null) return;
        var details = await _loadDetails(item).ConfigureAwait(false);
        BeginInvokeOnMainThread(() =>
        {
            if (generation != _generation || _items.Count == 0 || _items[_index].EpisodeId != item.EpisodeId) return;
            var people = details is null
                ? []
                : Split(details.Hosts).Concat(Split(details.Guests))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase).Take(3).ToArray();
            var topics = details is null
                ? []
                : details.Topics.Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.CurrentCultureIgnoreCase).Take(3).ToArray();
            if (people.Length == 0) ConfigurePlaceholder(_people, "No people listed", RadioVaultTheme.ActivityBlue);
            else ConfigurePills(_people, people, RadioVaultTheme.ActivityBlue);
            if (topics.Length == 0) ConfigurePlaceholder(_topics, "No topics listed", RadioVaultTheme.Research);
            else ConfigurePills(_topics, topics, RadioVaultTheme.Research);
        });
    }

    private void ConfigurePips()
    {
        Clear(_pips);
        var compact = _items.Count > 12;
        var veryCompact = _items.Count > 24;
        var diameter = veryCompact ? 3d : compact ? 4d : 6d;
        _pips.Spacing = veryCompact ? 2 : compact ? 3 : 6;
        for (var index = 0; index < _items.Count; index++)
        {
            var active = index == _index;
            var pip = new UIView { BackgroundColor = active ? RadioVaultTheme.Accent : RadioVaultTheme.Border };
            pip.Layer.CornerRadius = (nfloat)(diameter / 2d);
            pip.WidthAnchor.ConstraintEqualTo((nfloat)(active ? diameter * 2.25d : diameter)).Active = true;
            pip.HeightAnchor.ConstraintEqualTo((nfloat)diameter).Active = true;
            _pips.AddArrangedSubview(pip);
        }
    }

    private void ConfigurePills(UIStackView row, IReadOnlyList<string> values, UIColor color)
    {
        Clear(row);
        foreach (var value in values)
        {
            var button = UIButton.FromType(UIButtonType.System);
            button.SetTitle(value, UIControlState.Normal);
            button.SetTitleColor(color, UIControlState.Normal);
            button.TitleLabel!.Font = RadioVaultAccessibility.ScaledFont(10, UIFontWeight.Semibold);
            button.TitleLabel.AdjustsFontSizeToFitWidth = true;
            button.TitleLabel.MinimumScaleFactor = 0.72f;
            button.BackgroundColor = color.ColorWithAlpha(0.14f);
            button.Layer.CornerRadius = 14;
            button.Layer.BorderWidth = 1;
            button.Layer.BorderColor = color.ColorWithAlpha(0.65f).CGColor;
            button.HeightAnchor.ConstraintEqualTo(28).Active = true;
            button.TouchUpInside += (_, _) => _openEntity?.Invoke(value);
            row.AddArrangedSubview(button);
        }
    }

    private static void ConfigurePlaceholder(UIStackView row, string text, UIColor color)
    {
        Clear(row);
        var label = new UILabel
        {
            Text = text,
            Font = RadioVaultAccessibility.ScaledFont(10, UIFontWeight.Medium),
            TextColor = color.ColorWithAlpha(0.8f),
            TextAlignment = UITextAlignment.Center
        };
        label.HeightAnchor.ConstraintEqualTo(28).Active = true;
        row.AddArrangedSubview(label);
    }

    private void OpenCurrent()
    {
        if (_items.Count > 0) _openBroadcast?.Invoke(_items[_index]);
    }

    private void PlayCurrent()
    {
        if (_session is not null && _items.Count > 0) _ = _session.PlayAsync(_items[_index]);
    }

    private void StartTimer()
    {
        _timer?.Invalidate();
        _timer?.Dispose();
        _timer = _items.Count > 1 && !RadioVaultAccessibility.ReduceMotion
            ? NSTimer.CreateScheduledTimer(6d, true, _ => Advance())
            : null;
    }

    private static void Clear(UIStackView stack)
    {
        foreach (var view in stack.ArrangedSubviews)
        {
            stack.RemoveArrangedSubview(view);
            view.RemoveFromSuperview();
            view.Dispose();
        }
    }

    private static string[] Split(string value)
        => value.Split([',', ';', '|', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static UILabel SectionHeading(string value) => new()
    {
        Text = value,
        Font = RadioVaultAccessibility.ScaledFont(9, UIFontWeight.Bold),
        TextColor = RadioVaultTheme.MutedText
    };

    private static UIStackView PillRow() => new()
    {
        Axis = UILayoutConstraintAxis.Horizontal,
        Alignment = UIStackViewAlignment.Fill,
        Distribution = UIStackViewDistribution.FillEqually,
        Spacing = 6
    };

    public override void PrepareForReuse()
    {
        base.PrepareForReuse();
        _generation++;
        _timer?.Invalidate();
        _timer?.Dispose();
        _timer = null;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _generation++;
            _timer?.Invalidate();
            _timer?.Dispose();
            _timer = null;
        }
        base.Dispose(disposing);
    }
}
