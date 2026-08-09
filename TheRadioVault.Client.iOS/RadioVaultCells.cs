using Foundation;
using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Web.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

internal sealed class DashboardHeaderCell : UITableViewCell
{
    public DashboardHeaderCell() : base(UITableViewCellStyle.Default, "dashboard-header")
    {
        BackgroundColor = RadioVaultTheme.Background;
        SelectionStyle = UITableViewCellSelectionStyle.None;

        var title = new UILabel
        {
            Text = "Dashboard",
            Font = UIFont.SystemFontOfSize(32, UIFontWeight.Bold)!,
            TextColor = RadioVaultTheme.Text
        };
        var subtitle = new UILabel
        {
            Text = "Continue listening, rediscover the archive, or choose something unexpected.",
            Font = UIFont.SystemFontOfSize(13)!,
            TextColor = RadioVaultTheme.MutedText,
            Lines = 0
        };
        var stack = new UIStackView([title, subtitle])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 4,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        ContentView.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints([
            stack.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 4),
            stack.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -4),
            stack.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 8),
            stack.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -8)
        ]);
    }
}

internal sealed class DashboardStatsCell : UITableViewCell
{
    private readonly UIImageView[] _icons = new UIImageView[4];
    private readonly UILabel[] _titles = new UILabel[4];
    private readonly UILabel[] _values = new UILabel[4];

    public DashboardStatsCell() : base(UITableViewCellStyle.Default, "dashboard-stats")
    {
        BackgroundColor = UIColor.Clear;
        SelectionStyle = UITableViewCellSelectionStyle.None;

        var cards = new UIView[4];
        for (var index = 0; index < cards.Length; index++)
        {
            var icon = new UIImageView { ContentMode = UIViewContentMode.ScaleAspectFit };
            var value = new UILabel
            {
                Font = UIFont.SystemFontOfSize(19, UIFontWeight.Bold)!,
                TextColor = RadioVaultTheme.Text,
                TextAlignment = UITextAlignment.Center,
                AdjustsFontSizeToFitWidth = true,
                MinimumScaleFactor = 0.7f
            };
            var title = new UILabel
            {
                Font = UIFont.SystemFontOfSize(10, UIFontWeight.Semibold)!,
                TextColor = RadioVaultTheme.MutedText,
                TextAlignment = UITextAlignment.Center,
                Lines = 1,
                AdjustsFontSizeToFitWidth = true,
                MinimumScaleFactor = 0.65f
            };
            var content = new UIStackView([icon, value, title])
            {
                Axis = UILayoutConstraintAxis.Vertical,
                Alignment = UIStackViewAlignment.Center,
                Distribution = UIStackViewDistribution.Fill,
                Spacing = 3,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            var card = new UIView { BackgroundColor = RadioVaultTheme.Surface };
            card.Layer.CornerRadius = 12;
            card.Layer.BorderColor = RadioVaultTheme.Border.CGColor;
            card.Layer.BorderWidth = 1;
            card.AddSubview(content);
            NSLayoutConstraint.ActivateConstraints([
                icon.WidthAnchor.ConstraintEqualTo(23),
                icon.HeightAnchor.ConstraintEqualTo(23),
                content.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor, 3),
                content.TrailingAnchor.ConstraintEqualTo(card.TrailingAnchor, -3),
                content.CenterYAnchor.ConstraintEqualTo(card.CenterYAnchor)
            ]);
            cards[index] = card;
            _icons[index] = icon;
            _titles[index] = title;
            _values[index] = value;
        }

        var row = new UIStackView(cards)
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.FillEqually,
            Spacing = 7,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        ContentView.AddSubview(row);
        NSLayoutConstraint.ActivateConstraints([
            row.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor),
            row.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor),
            row.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 4),
            row.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -4),
            row.HeightAnchor.ConstraintEqualTo(92)
        ]);
    }

    public void Configure(params (string Title, int Value, RadioVaultIcon Icon)[] stats)
    {
        for (var index = 0; index < _titles.Length; index++)
        {
            var stat = stats[index];
            _titles[index].Text = stat.Title;
            _values[index].Text = stat.Value.ToString("N0");
            _icons[index].Image = RadioVaultIcons.Image(stat.Icon, size: 23);
        }
    }
}

internal sealed class DashboardContinueCell : UITableViewCell
{
    private readonly UILabel _collection = new();
    private readonly UILabel _date = new();
    private readonly UILabel _title = new();
    private readonly UILabel _progressText = new();
    private readonly UIProgressView _progress = new(UIProgressViewStyle.Default);
    private readonly UIButton _resume = UIButton.FromType(UIButtonType.System);
    private Action? _resumeAction;

    public DashboardContinueCell() : base(UITableViewCellStyle.Default, "dashboard-continue")
    {
        BackgroundColor = RadioVaultTheme.SurfaceRaised;
        SelectionStyle = UITableViewCellSelectionStyle.None;

        var artwork = new UIView { BackgroundColor = RadioVaultTheme.AccentSubtle };
        artwork.Layer.CornerRadius = 14;
        artwork.Layer.BorderColor = RadioVaultTheme.Border.CGColor;
        artwork.Layer.BorderWidth = 1;
        var artworkIcon = new UIImageView(RadioVaultIcons.Image(RadioVaultIcon.Radio, size: 48))
        {
            ContentMode = UIViewContentMode.Center,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        artwork.AddSubview(artworkIcon);
        NSLayoutConstraint.ActivateConstraints([
            artworkIcon.CenterXAnchor.ConstraintEqualTo(artwork.CenterXAnchor),
            artworkIcon.CenterYAnchor.ConstraintEqualTo(artwork.CenterYAnchor)
        ]);

        _collection.Font = UIFont.SystemFontOfSize(21, UIFontWeight.Bold)!;
        _collection.TextColor = RadioVaultTheme.Text;
        _collection.Lines = 2;
        _date.Font = UIFont.SystemFontOfSize(12)!;
        _date.TextColor = RadioVaultTheme.MutedText;
        _title.Font = UIFont.ItalicSystemFontOfSize(16)!;
        _title.TextColor = RadioVaultTheme.Text;
        _title.Lines = 2;
        _progressText.Font = UIFont.MonospacedDigitSystemFontOfSize(11, UIFontWeight.Medium)!;
        _progressText.TextColor = RadioVaultTheme.MutedText;
        _progressText.TextAlignment = UITextAlignment.Right;
        _progress.ProgressTintColor = RadioVaultTheme.Progress;
        _progress.TrackTintColor = RadioVaultTheme.Border;

        _resume.SetTitle("Resume", UIControlState.Normal);
        _resume.SetTitleColor(RadioVaultTheme.Background, UIControlState.Normal);
        _resume.TitleLabel!.Font = UIFont.SystemFontOfSize(15, UIFontWeight.Bold)!;
        _resume.BackgroundColor = RadioVaultTheme.Accent;
        _resume.Layer.CornerRadius = 10;
        _resume.TouchUpInside += (_, _) => _resumeAction?.Invoke();

        var metadata = new UIStackView([_collection, _date, _title])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 5
        };
        var hero = new UIStackView([metadata, artwork])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 14
        };
        var progressRow = new UIStackView([_progress, _progressText])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 10
        };
        var content = new UIStackView([hero, progressRow, _resume])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 14,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        ContentView.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints([
            content.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 18),
            content.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -18),
            content.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 18),
            content.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -18),
            artwork.WidthAnchor.ConstraintEqualTo(90),
            artwork.HeightAnchor.ConstraintEqualTo(90),
            _progressText.WidthAnchor.ConstraintEqualTo(76),
            _resume.HeightAnchor.ConstraintEqualTo(42)
        ]);
    }

    public void Configure(MobileBroadcastItem item, Action resume)
    {
        _collection.Text = item.Source.CollectionName;
        _date.Text = item.Source.AirDate?.ToString("dddd, d MMMM yyyy") ?? "Date unknown";
        _title.Text = item.Title;
        _progress.Progress = (float)(item.DisplayProgress / 100d);
        _progressText.Text = $"{item.DisplayProgress:0}% listened";
        _resumeAction = resume;
        AccessibilityLabel = $"Continue listening, {item.Title}, {item.DisplayProgress:0} percent listened";
    }
}

internal sealed class BroadcastProgressCell : UITableViewCell
{
    private readonly UILabel _title = new();
    private readonly UILabel _subtitle = new();
    private readonly UILabel _percentage = new();
    private readonly UIProgressView _progress = new(UIProgressViewStyle.Default);

    public BroadcastProgressCell(string identifier) : base(UITableViewCellStyle.Default, identifier)
    {
        BackgroundColor = RadioVaultTheme.Surface;
        TintColor = RadioVaultTheme.Accent;

        _title.Font = UIFont.SystemFontOfSize(16, UIFontWeight.Semibold)!;
        _title.TextColor = RadioVaultTheme.Text;
        _title.Lines = 2;
        _subtitle.Font = UIFont.SystemFontOfSize(12)!;
        _subtitle.TextColor = RadioVaultTheme.MutedText;
        _subtitle.Lines = 2;
        _percentage.Font = UIFont.MonospacedDigitSystemFontOfSize(10, UIFontWeight.Medium)!;
        _percentage.TextColor = RadioVaultTheme.MutedText;
        _percentage.TextAlignment = UITextAlignment.Right;
        _progress.ProgressTintColor = RadioVaultTheme.Progress;
        _progress.TrackTintColor = RadioVaultTheme.Border;

        var labels = new UIStackView([_title, _subtitle])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 3
        };
        var progressRow = new UIStackView([_progress, _percentage])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 10
        };
        var content = new UIStackView([labels, progressRow])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        ContentView.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints([
            content.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 16),
            content.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -16),
            content.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 11),
            content.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -11),
            _percentage.WidthAnchor.ConstraintEqualTo(42)
        ]);
    }

    public void Configure(MobileBroadcastItem item, string? detail = null)
    {
        Configure(
            item.Title,
            detail ?? $"{item.Subtitle} · {item.Status}",
            item.DisplayProgress,
            item.Source.Completed);
    }

    public void Configure(WebEpisode item, string detail)
    {
        var completed = item.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase);
        Configure(item.Title, detail, completed ? 100d : item.ProgressPercent, completed);
    }

    private void Configure(string title, string detail, double progress, bool completed)
    {
        _title.Text = title;
        _subtitle.Text = detail;
        _percentage.Text = $"{progress:0}%";
        _progress.Progress = (float)(progress / 100d);
        _progress.ProgressTintColor = completed ? RadioVaultTheme.Completed : RadioVaultTheme.Progress;
        AccessibilityValue = $"{progress:0} percent listened";
    }
}

internal sealed class ArchiveGridRowCell : UITableViewCell
{
    private readonly ArchiveTileControl _leading = new();
    private readonly ArchiveTileControl _trailing = new();

    public ArchiveGridRowCell() : base(UITableViewCellStyle.Default, "archive-grid-row")
    {
        BackgroundColor = UIColor.Clear;
        SelectionStyle = UITableViewCellSelectionStyle.None;
        var stack = new UIStackView([_leading, _trailing])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.FillEqually,
            Spacing = 11,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        ContentView.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints([
            stack.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor),
            stack.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor),
            stack.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 5),
            stack.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -5),
            stack.HeightAnchor.ConstraintEqualTo(170)
        ]);
    }

    public void Configure(
        WebClientLibraryArchivePeriodSummary leading,
        WebClientLibraryArchivePeriodSummary? trailing,
        Action<WebClientLibraryArchivePeriodSummary> selected)
    {
        _leading.Configure(leading, selected);
        _trailing.Hidden = trailing is null;
        if (trailing is not null) _trailing.Configure(trailing, selected);
    }
}

internal sealed class ArchiveTileControl : UIControl
{
    private readonly UILabel _shows = new();
    private readonly UILabel _title = new();
    private readonly UILabel _count = new();
    private readonly UILabel _progressText = new();
    private readonly UIProgressView _progress = new(UIProgressViewStyle.Default);
    private WebClientLibraryArchivePeriodSummary? _period;
    private Action<WebClientLibraryArchivePeriodSummary>? _selected;

    public ArchiveTileControl()
    {
        BackgroundColor = RadioVaultTheme.SurfaceRaised;
        Layer.CornerRadius = 13;
        Layer.BorderColor = RadioVaultTheme.Border.CGColor;
        Layer.BorderWidth = 1;

        var radio = new UIImageView(RadioVaultIcons.Image(RadioVaultIcon.Radio, RadioVaultTheme.SubtleText, 48, 1.5))
        {
            ContentMode = UIViewContentMode.ScaleAspectFit,
            Alpha = 0.45f,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _shows.Font = UIFont.SystemFontOfSize(10, UIFontWeight.Semibold)!;
        _shows.TextColor = RadioVaultTheme.MutedText;
        _shows.Lines = 1;
        _shows.LineBreakMode = UILineBreakMode.TailTruncation;
        _title.Font = UIFont.SystemFontOfSize(27, UIFontWeight.Bold)!;
        _title.TextColor = RadioVaultTheme.Text;
        _title.Lines = 2;
        _title.AdjustsFontSizeToFitWidth = true;
        _title.MinimumScaleFactor = 0.7f;
        _count.Font = UIFont.SystemFontOfSize(12, UIFontWeight.Semibold)!;
        _count.TextColor = RadioVaultTheme.Text;
        _progressText.Font = UIFont.SystemFontOfSize(10)!;
        _progressText.TextColor = RadioVaultTheme.MutedText;
        _progress.ProgressTintColor = RadioVaultTheme.Progress;
        _progress.TrackTintColor = RadioVaultTheme.Border;

        var content = new UIStackView([_shows, _title, _count, _progressText, _progress])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 5,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        AddSubview(radio);
        AddSubview(content);
        NSLayoutConstraint.ActivateConstraints([
            radio.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -9),
            radio.TopAnchor.ConstraintEqualTo(TopAnchor, 10),
            radio.WidthAnchor.ConstraintEqualTo(48),
            radio.HeightAnchor.ConstraintEqualTo(48),
            content.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 13),
            content.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -13),
            content.TopAnchor.ConstraintEqualTo(TopAnchor, 13),
            content.BottomAnchor.ConstraintEqualTo(BottomAnchor, -13)
        ]);
        TouchUpInside += (_, _) =>
        {
            if (_period is not null) _selected?.Invoke(_period);
        };
    }

    public void Configure(
        WebClientLibraryArchivePeriodSummary period,
        Action<WebClientLibraryArchivePeriodSummary> selected)
    {
        _period = period;
        _selected = selected;
        _shows.Text = period.ShowsText;
        _title.Text = period.Title;
        _count.Text = $"{period.BroadcastCount:N0} broadcasts";
        _progressText.Text = $"{period.ProgressPercent}% listened · {period.FavouriteCount:N0} favourites";
        _progress.Progress = period.ProgressPercent / 100f;
        AccessibilityLabel = $"{period.Title}, {period.BroadcastCount} broadcasts, {period.ProgressPercent} percent listened";
        AccessibilityTraits = UIAccessibilityTrait.Button;
    }
}

internal sealed class BroadcastHeroCell : UITableViewCell
{
    private readonly UILabel _collection = new();
    private readonly UILabel _title = new();
    private readonly UILabel _date = new();
    private readonly UILabel _status = new();
    private readonly UIProgressView _progress = new(UIProgressViewStyle.Default);

    public BroadcastHeroCell() : base(UITableViewCellStyle.Default, "broadcast-hero")
    {
        BackgroundColor = RadioVaultTheme.SurfaceRaised;
        SelectionStyle = UITableViewCellSelectionStyle.None;
        var artwork = new UIView { BackgroundColor = RadioVaultTheme.AccentSubtle };
        artwork.Layer.CornerRadius = 16;
        var icon = new UIImageView(RadioVaultIcons.Image(RadioVaultIcon.Radio, size: 52))
        {
            ContentMode = UIViewContentMode.Center,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        artwork.AddSubview(icon);
        NSLayoutConstraint.ActivateConstraints([
            icon.CenterXAnchor.ConstraintEqualTo(artwork.CenterXAnchor),
            icon.CenterYAnchor.ConstraintEqualTo(artwork.CenterYAnchor)
        ]);

        _collection.Font = UIFont.SystemFontOfSize(13, UIFontWeight.Semibold)!;
        _collection.TextColor = RadioVaultTheme.Accent;
        _title.Font = UIFont.SystemFontOfSize(24, UIFontWeight.Bold)!;
        _title.TextColor = RadioVaultTheme.Text;
        _title.Lines = 3;
        _date.Font = UIFont.SystemFontOfSize(13)!;
        _date.TextColor = RadioVaultTheme.MutedText;
        _status.Font = UIFont.MonospacedDigitSystemFontOfSize(11, UIFontWeight.Medium)!;
        _status.TextColor = RadioVaultTheme.MutedText;
        _progress.ProgressTintColor = RadioVaultTheme.Progress;
        _progress.TrackTintColor = RadioVaultTheme.Border;

        var labels = new UIStackView([_collection, _title, _date])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 6
        };
        var top = new UIStackView([artwork, labels])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 14
        };
        var content = new UIStackView([top, _status, _progress])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 10,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        ContentView.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints([
            content.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 18),
            content.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -18),
            content.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 18),
            content.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -18),
            artwork.WidthAnchor.ConstraintEqualTo(92),
            artwork.HeightAnchor.ConstraintEqualTo(92)
        ]);
    }

    public void Configure(MobileBroadcastItem item)
    {
        _collection.Text = item.Source.CollectionName.ToUpperInvariant();
        _title.Text = item.Title;
        _date.Text = item.Source.AirDate?.ToString("dddd, d MMMM yyyy") ?? "Date unknown";
        _status.Text = item.Status;
        _progress.Progress = (float)(item.DisplayProgress / 100d);
        _progress.ProgressTintColor = item.Source.Completed ? RadioVaultTheme.Completed : RadioVaultTheme.Progress;
    }
}

internal sealed class BroadcastActionStripCell : UITableViewCell
{
    private readonly UIButton _play = ActionButton();
    private readonly UIButton _favourite = ActionButton();
    private readonly UIButton _download = ActionButton();
    private Action? _playAction;
    private Action? _favouriteAction;
    private Action? _downloadAction;

    public BroadcastActionStripCell() : base(UITableViewCellStyle.Default, "broadcast-action-strip")
    {
        BackgroundColor = RadioVaultTheme.Surface;
        SelectionStyle = UITableViewCellSelectionStyle.None;
        _play.TouchUpInside += (_, _) => _playAction?.Invoke();
        _favourite.TouchUpInside += (_, _) => _favouriteAction?.Invoke();
        _download.TouchUpInside += (_, _) => _downloadAction?.Invoke();
        var row = new UIStackView([_play, _favourite, _download])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.FillEqually,
            Spacing = 9,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        ContentView.AddSubview(row);
        NSLayoutConstraint.ActivateConstraints([
            row.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 12),
            row.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -12),
            row.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 10),
            row.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -10),
            row.HeightAnchor.ConstraintEqualTo(48)
        ]);
    }

    public void Configure(
        MobileBroadcastItem item,
        bool downloaded,
        Action play,
        Action favourite,
        Action download)
    {
        ConfigureButton(_play, item.HasProgress ? "Resume" : "Play", RadioVaultIcon.Play, RadioVaultTheme.Progress);
        ConfigureButton(_favourite, item.Source.Favourite ? "Favourited" : "Favourite", RadioVaultIcon.Favourite, RadioVaultTheme.Favourite);
        ConfigureButton(_download, downloaded ? "Remove" : "Download", RadioVaultIcon.Download,
            downloaded ? RadioVaultTheme.Danger : RadioVaultTheme.Settings);
        _playAction = play;
        _favouriteAction = favourite;
        _downloadAction = download;
    }

    private static UIButton ActionButton()
    {
        var button = UIButton.FromType(UIButtonType.System);
        button.BackgroundColor = RadioVaultTheme.SurfaceRaised;
        button.Layer.CornerRadius = 10;
        button.TitleLabel!.Font = UIFont.SystemFontOfSize(11, UIFontWeight.Semibold)!;
        return button;
    }

    private static void ConfigureButton(UIButton button, string title, RadioVaultIcon icon, UIColor color)
    {
        button.SetTitle(title, UIControlState.Normal);
        button.SetTitleColor(color, UIControlState.Normal);
        button.SetImage(RadioVaultIcons.Image(icon, color, 18, 1.8), UIControlState.Normal);
    }
}
