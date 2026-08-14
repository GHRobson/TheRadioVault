using TheRadioVault.Client.Mobile;
using TheRadioVault.Web.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

internal sealed class LiveRadioDashboardCell : UITableViewCell
{
    private readonly UIImageView _icon = new();
    private readonly UILabel _eyebrow = new();
    private readonly UILabel _title = new();
    private readonly UILabel _detail = new();
    private readonly UILabel _liveBadge = new();

    public LiveRadioDashboardCell() : base(UITableViewCellStyle.Default, "live-radio-dashboard")
    {
        BackgroundColor = RadioVaultTheme.AccentSubtle;
        SelectionStyle = UITableViewCellSelectionStyle.Default;
        Accessory = UITableViewCellAccessory.DisclosureIndicator;
        Layer.CornerRadius = 18;
        Layer.MasksToBounds = true;

        _icon.Image = RadioVaultIcons.Image(RadioVaultIcon.Radio, RadioVaultTheme.Accent, 34, 2.1);
        _icon.ContentMode = UIViewContentMode.Center;
        _eyebrow.Text = "RADIO VAULT LIVE";
        _eyebrow.TextColor = RadioVaultTheme.Accent;
        _eyebrow.Font = RadioVaultAccessibility.ScaledFont(11, UIFontWeight.Bold);
        _title.TextColor = RadioVaultTheme.Text;
        _title.Font = RadioVaultAccessibility.ScaledFont(18, UIFontWeight.Bold);
        _title.Lines = 2;
        _detail.TextColor = RadioVaultTheme.MutedText;
        _detail.Font = UIFont.PreferredFootnote!;
        _detail.Lines = 2;
        _liveBadge.Text = " LIVE ";
        _liveBadge.TextColor = RadioVaultTheme.Background;
        _liveBadge.BackgroundColor = RadioVaultTheme.Accent;
        _liveBadge.Font = RadioVaultAccessibility.ScaledFont(10, UIFontWeight.Heavy);
        _liveBadge.TextAlignment = UITextAlignment.Center;
        _liveBadge.Layer.CornerRadius = 7;
        _liveBadge.Layer.MasksToBounds = true;

        var labels = new UIStackView([_eyebrow, _title, _detail])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 3
        };
        var row = new UIStackView([_icon, labels, _liveBadge])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 12,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        ContentView.AddSubview(row);
        NSLayoutConstraint.ActivateConstraints([
            row.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 14),
            row.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -12),
            row.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 14),
            row.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -14),
            _icon.WidthAnchor.ConstraintEqualTo(42),
            _icon.HeightAnchor.ConstraintEqualTo(42),
            _liveBadge.WidthAnchor.ConstraintGreaterThanOrEqualTo(42),
            _liveBadge.HeightAnchor.ConstraintEqualTo(22)
        ]);
    }

    public void Configure(WebLiveRadioSnapshot? station, bool tunedIn, bool loading)
    {
        _title.Text = station?.Current is { } current
            ? string.IsNullOrWhiteSpace(current.Broadcast.Title) ? current.Broadcast.CollectionName : current.Broadcast.Title
            : "Your archive, broadcasting now";
        _detail.Text = loading
            ? "Tuning in…"
            : station?.Current is { } programme
                ? $"{programme.Broadcast.CollectionName} · {programme.SelectionReason}"
                : "Open the station to see what is on air.";
        _liveBadge.Text = tunedIn ? " ON AIR " : " LIVE ";
        AccessibilityLabel = $"Radio Vault Live. {_title.Text}. {_detail.Text}";
    }
}

internal sealed class LiveRadioOnAirCell : UITableViewCell
{
    private readonly UILabel _badge = new();
    private readonly UILabel _title = new();
    private readonly UILabel _show = new();
    private readonly UILabel _reason = new();
    private readonly UIProgressView _progress = new(UIProgressViewStyle.Default);
    private readonly UILabel _time = new();
    private readonly UIButton _tune = UIButton.FromType(UIButtonType.System);
    private readonly UIButton _moment = UIButton.FromType(UIButtonType.System);
    private readonly RadioVaultOutputRouteView _outputRoute = new();

    public LiveRadioOnAirCell() : base(UITableViewCellStyle.Default, "live-radio-on-air")
    {
        SelectionStyle = UITableViewCellSelectionStyle.None;
        BackgroundColor = RadioVaultTheme.Surface;
        _badge.Text = "● LIVE";
        _badge.TextColor = RadioVaultTheme.Accent;
        _badge.Font = RadioVaultAccessibility.ScaledFont(12, UIFontWeight.Heavy);
        _title.TextColor = RadioVaultTheme.Text;
        _title.Font = UIFont.PreferredTitle2!;
        _title.Lines = 0;
        _show.TextColor = RadioVaultTheme.MutedText;
        _show.Font = UIFont.PreferredHeadline!;
        _show.Lines = 2;
        _reason.TextColor = RadioVaultTheme.SubtleText;
        _reason.Font = UIFont.PreferredFootnote!;
        _reason.Lines = 0;
        _progress.ProgressTintColor = RadioVaultTheme.Progress;
        _progress.TrackTintColor = RadioVaultTheme.Border;
        _progress.AccessibilityLabel = "Live programme position";
        _time.TextColor = RadioVaultTheme.MutedText;
        _time.Font = RadioVaultAccessibility.ScaledMonospacedDigitFont(12, UIFontWeight.Semibold);
        ConfigureButton(_tune, RadioVaultTheme.Accent, RadioVaultTheme.Background);
        ConfigureButton(_moment, RadioVaultTheme.SurfaceRaised, RadioVaultTheme.Text);

        var buttons = new UIStackView([_tune, _moment])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Distribution = UIStackViewDistribution.FillEqually,
            Spacing = 10
        };
        var stack = new UIStackView([_badge, _title, _show, _reason, _progress, _time, _outputRoute, buttons])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 10,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        ContentView.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints([
            stack.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 18),
            stack.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -18),
            stack.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 18),
            stack.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -18),
            _outputRoute.HeightAnchor.ConstraintGreaterThanOrEqualTo(44),
            buttons.HeightAnchor.ConstraintGreaterThanOrEqualTo(46)
        ]);
        RadioVaultAccessibility.PrepareView(this);
    }

    public void Configure(
        WebLiveRadioProgramme programme,
        bool tunedIn,
        bool loading,
        Action tuneAction,
        Func<Task<bool>> momentAction)
    {
        var broadcast = programme.Broadcast;
        _title.Text = string.IsNullOrWhiteSpace(broadcast.Title) ? broadcast.CollectionName : broadcast.Title;
        _show.Text = broadcast.AirDate is { } date
            ? $"{broadcast.CollectionName} · {date:dd MMM yyyy}"
            : broadcast.CollectionName;
        _reason.Text = programme.SelectionReason;
        var duration = Math.Max(1, broadcast.DurationMs);
        _progress.Progress = (float)Math.Clamp(programme.PositionMs / (double)duration, 0, 1);
        _progress.AccessibilityValue = $"{Math.Round(_progress.Progress * 100)} percent";
        _time.Text = $"{Format(programme.PositionMs)}  ·  live  ·  {Format(programme.RemainingMs)} remaining";
        _tune.SetTitle(loading ? "Tuning…" : tunedIn ? "Leave Live Radio" : "Tune In", UIControlState.Normal);
        _tune.Enabled = !loading;
        _moment.SetTitle("Save This Moment", UIControlState.Normal);
        _moment.Enabled = tunedIn && !loading;
        _tune.TouchUpInside += (_, _) => tuneAction();
        _moment.TouchUpInside += async (_, _) =>
        {
            _moment.Enabled = false;
            _moment.SetTitle("Saving…", UIControlState.Normal);
            var saved = await momentAction().ConfigureAwait(false);
            BeginInvokeOnMainThread(() =>
            {
                _moment.SetTitle(saved ? "Moment Saved" : "Try Again", UIControlState.Normal);
                _moment.Enabled = tunedIn;
                RadioVaultAccessibility.Announce(saved ? "Moment saved" : "Moment could not be saved");
            });
        };
    }

    private static void ConfigureButton(UIButton button, UIColor background, UIColor foreground)
    {
        button.BackgroundColor = background;
        button.SetTitleColor(foreground, UIControlState.Normal);
        button.TitleLabel!.Font = RadioVaultAccessibility.ScaledFont(15, UIFontWeight.Bold);
        button.Layer.CornerRadius = 16;
    }

    private static string Format(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }
}
