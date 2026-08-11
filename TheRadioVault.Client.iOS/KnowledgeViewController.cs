using CoreGraphics;
using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class KnowledgeViewController : SessionTableViewController
{
    private bool _loading;
    private MobileKnowledgeSnapshot? Snapshot => Session.Knowledge;
    private IReadOnlyList<MobileKnowledgeCollection> Collections => Snapshot?.Collections
        .Where(value => value.CollectionId.HasValue)
        .OrderByDescending(value => value.RecordCount)
        .ThenBy(value => value.Name)
        .ToArray() ?? [];

    public KnowledgeViewController(MobileClientSession session) : base(session) => Title = "Knowledge";
    protected override string? PageHeading => "Knowledge";
    protected override string PageDescription => "Coverage, evidence and decisions.";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 74;
        RefreshControl = new UIRefreshControl();
        RefreshControl.ValueChanged += async (_, _) =>
        {
            await LoadAsync();
            BeginInvokeOnMainThread(() => RefreshControl?.EndRefreshing());
        };
        _ = LoadAsync();
    }

    public override nint NumberOfSections(UITableView tableView) => Snapshot is null ? 1 : 3;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => Snapshot is null ? 1 : 4,
        1 => Math.Max(1, Collections.Count),
        _ => 1
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        0 => Snapshot is null ? null : "Knowledge dashboard",
        1 => "Coverage heat maps",
        _ => "Triage"
    };

    public override string? TitleForFooter(UITableView tableView, nint section) => section switch
    {
        1 when Snapshot?.IsLibraryFallback == true => "Coverage is built from the complete catalogue saved on this iPhone. Update the paired server to add research evidence.",
        1 => "Open a show to see daily metadata coverage, gaps and known missing broadcasts.",
        2 when Snapshot?.IsLibraryFallback == true => "Research decisions require the current Radio Vault Server Knowledge service.",
        2 => "Swipe right to accept, left to keep the current date, or up to ignore a suggestion.",
        _ => null
    };

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
        {
            if (Snapshot is null)
                return IconDetailCell(
                    "knowledge-empty",
                    _loading ? "Loading Knowledge…" : "Retry Knowledge",
                    Session.KnowledgeStatusText,
                    _loading ? RadioVaultIcon.Knowledge : RadioVaultIcon.Sync,
                    disclosure: !_loading);
            var overview = Snapshot.Overview;
            var values = Snapshot.IsLibraryFallback ? new[]
            {
                ("Library broadcasts", $"{overview.TotalRecords:N0} broadcasts cached on this iPhone", RadioVaultIcon.Knowledge),
                ("Available offline", $"{overview.InLibraryRecords:N0} catalogue records ready", RadioVaultIcon.Library),
                ("Needs attention", $"{overview.NeedsReviewRecords:N0} broadcasts need metadata attention", RadioVaultIcon.InProgress),
                ("Summary coverage", $"{overview.WithSummaries:N0} broadcasts have descriptions", RadioVaultIcon.Completed)
            } : new[]
            {
                ("Research records", $"{overview.TotalRecords:N0} archived records", RadioVaultIcon.Knowledge),
                ("Linked to Library", $"{overview.InLibraryRecords:N0} records have broadcasts", RadioVaultIcon.Library),
                ("Needs review", $"{overview.NeedsReviewRecords:N0} records need attention", RadioVaultIcon.InProgress),
                ("Core coverage", $"{overview.CoveragePercent:N0}% across summaries, people, topics and sources", RadioVaultIcon.Completed)
            };
            var value = values[indexPath.Row];
            return IconCell("knowledge-stat", value.Item1, value.Item2, value.Item3, disclosure: false);
        }

        if (indexPath.Section == 1)
        {
            if (Collections.Count == 0)
                return DetailCell("knowledge-coverage-empty", "No coverage maps yet", "Dated research records will appear here.");
            var collection = Collections[indexPath.Row];
            return IconCell(
                "knowledge-coverage",
                collection.Name,
                Snapshot?.IsLibraryFallback == true
                    ? $"{collection.RecordCount:N0} saved broadcast{(collection.RecordCount == 1 ? string.Empty : "s")}"
                    : $"{collection.RecordCount:N0} knowledge record{(collection.RecordCount == 1 ? string.Empty : "s")}",
                RadioVaultIcon.Grid,
                disclosure: true);
        }

        var count = Snapshot?.DateReviews.Count ?? 0;
        return IconCell(
            "knowledge-triage",
            Snapshot?.IsLibraryFallback == true ? "Research decisions" : "Review date suggestions",
            Snapshot?.IsLibraryFallback == true
                ? "Update the paired server to enable triage"
                : count == 0 ? "Nothing is waiting for a decision" : $"{count:N0} suggestion{(count == 1 ? string.Empty : "s")} waiting",
            RadioVaultIcon.InProgress,
            disclosure: Snapshot?.IsLibraryFallback != true);
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (Snapshot is null && indexPath.Section == 0)
            _ = LoadAsync();
        else if (indexPath.Section == 1 && indexPath.Row < Collections.Count && Collections[indexPath.Row].CollectionId is { } id)
            NavigationController?.PushViewController(
                new KnowledgeCoverageViewController(Session, id, Collections[indexPath.Row].Name), true);
        else if (indexPath.Section == 2 && Snapshot?.IsLibraryFallback != true)
            NavigationController?.PushViewController(new KnowledgeTriageViewController(Session), true);
    }

    private async Task LoadAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            await Session.LoadKnowledgeAsync().ConfigureAwait(false);
            BeginInvokeOnMainThread(() => TableView.ReloadData());
        }
        finally { _loading = false; }
    }

    private static UITableViewCell IconCell(
        string identifier,
        string title,
        string detail,
        RadioVaultIcon icon,
        bool disclosure)
    {
        return IconDetailCell(identifier, title, detail, icon, disclosure);
    }
}

public sealed class KnowledgeCoverageViewController : SessionTableViewController
{
    private readonly int _collectionId;
    private readonly string _showName;
    private MobileKnowledgeCoverage? _coverage;
    private IReadOnlyList<IGrouping<(int Year, int Month), MobileKnowledgeCoverageDay>> Months => _coverage?.Days
        .GroupBy(value => (value.Date.Year, value.Date.Month))
        .OrderByDescending(value => value.Key.Year)
        .ThenByDescending(value => value.Key.Month)
        .ToArray() ?? [];

    public KnowledgeCoverageViewController(MobileClientSession session, int collectionId, string showName) : base(session)
    {
        _collectionId = collectionId;
        _showName = showName;
        Title = "Coverage";
    }

    protected override string? PageHeading => _showName;
    protected override string PageDescription => "Knowledge coverage by broadcast day.";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        TableView.RowHeight = 76;
        TableView.SeparatorStyle = UITableViewCellSeparatorStyle.None;
        _ = LoadAsync();
    }

    public override nint NumberOfSections(UITableView tableView) => 1;
    public override nint RowsInSection(UITableView tableView, nint section) => Math.Max(1, Months.Count);
    public override string? TitleForHeader(UITableView tableView, nint section) => _coverage is null
        ? null
        : $"{_coverage.FirstDate:dd MMM yyyy} – {_coverage.LastDate:dd MMM yyyy}";
    public override string? TitleForFooter(UITableView tableView, nint section) => _coverage is null
        ? Session.StatusText
        : $"{_coverage.DatedBroadcastDays:N0} dated days · {_coverage.AverageMetadataScore:N0}% average metadata · {_coverage.GapDays:N0} weekday gaps";

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (Months.Count == 0) return DetailCell("coverage-empty", "Loading coverage…", Session.StatusText);
        var month = Months[indexPath.Row];
        var cell = new KnowledgeHeatMapCell();
        cell.Configure(month.Key.Year, month.Key.Month, month.ToArray());
        return cell;
    }

    private async Task LoadAsync()
    {
        var result = await Session.LoadKnowledgeCoverageAsync(_collectionId).ConfigureAwait(false);
        BeginInvokeOnMainThread(() =>
        {
            _coverage = result;
            TableView.ReloadData();
        });
    }
}

internal sealed class KnowledgeHeatMapCell : UITableViewCell
{
    private readonly UILabel _title = new();
    private readonly KnowledgeHeatStripView _strip = new();

    public KnowledgeHeatMapCell() : base(UITableViewCellStyle.Default, "knowledge-heat-map")
    {
        BackgroundColor = RadioVaultTheme.Surface;
        SelectionStyle = UITableViewCellSelectionStyle.None;
        _title.Font = UIFont.SystemFontOfSize(13, UIFontWeight.Semibold)!;
        _title.TextColor = RadioVaultTheme.Text;
        _title.TranslatesAutoresizingMaskIntoConstraints = false;
        _strip.TranslatesAutoresizingMaskIntoConstraints = false;
        ContentView.AddSubviews(_title, _strip);
        NSLayoutConstraint.ActivateConstraints([
            _title.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 16),
            _title.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 10),
            _title.WidthAnchor.ConstraintEqualTo(96),
            _strip.LeadingAnchor.ConstraintEqualTo(_title.TrailingAnchor, 8),
            _strip.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -16),
            _strip.CenterYAnchor.ConstraintEqualTo(ContentView.CenterYAnchor),
            _strip.HeightAnchor.ConstraintEqualTo(24)
        ]);
    }

    public void Configure(int year, int month, IReadOnlyList<MobileKnowledgeCoverageDay> days)
    {
        _title.Text = new DateTime(year, month, 1).ToString("MMM yyyy");
        _strip.Days = days;
    }
}

internal sealed class KnowledgeHeatStripView : UIView
{
    private IReadOnlyList<MobileKnowledgeCoverageDay> _days = [];
    public IReadOnlyList<MobileKnowledgeCoverageDay> Days
    {
        get => _days;
        set { _days = value; SetNeedsDisplay(); }
    }

    public override void Draw(CGRect rect)
    {
        base.Draw(rect);
        var context = UIGraphics.GetCurrentContext();
        if (context is null) return;
        var gap = 2d;
        var width = Math.Max(2d, (rect.Width - gap * 30) / 31d);
        var byDay = Days.ToDictionary(value => value.Date.Day);
        for (var day = 1; day <= 31; day++)
        {
            var color = byDay.TryGetValue(day, out var value) ? ColorFor(value) : RadioVaultTheme.SurfaceRaised;
            color.SetFill();
            var x = (day - 1) * (width + gap);
            using var path = UIBezierPath.FromRoundedRect(new CGRect(x, 2, width, rect.Height - 4), 2);
            path.Fill();
        }
    }

    private static UIColor ColorFor(MobileKnowledgeCoverageDay value)
    {
        if (value.IsKnownMissing) return RadioVaultTheme.Settings;
        if (!value.HasAudio && !value.HasResearch) return value.IsWeekend ? RadioVaultTheme.SurfaceHover : RadioVaultTheme.Danger;
        if (value.MetadataScore >= 80) return RadioVaultTheme.Completed;
        if (value.MetadataScore >= 50) return RadioVaultTheme.Accent;
        if (value.MetadataScore >= 25) return UIColor.FromRGB(0xE9, 0x94, 0x4A);
        return RadioVaultTheme.Danger;
    }
}

public sealed class KnowledgeTriageViewController : SessionTableViewController
{
    private MobileKnowledgeDateReview? _lastReview;
    private bool _resolving;
    private IReadOnlyList<MobileKnowledgeDateReview> Reviews => Session.Knowledge?.DateReviews ?? [];

    public KnowledgeTriageViewController(MobileClientSession session) : base(session) => Title = "Triage";
    protected override string? PageHeading => "Knowledge triage";
    protected override string PageDescription => "Make quick, reversible date decisions.";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 330;
        _ = Session.LoadKnowledgeAsync();
    }

    public override nint NumberOfSections(UITableView tableView) => 2;
    public override nint RowsInSection(UITableView tableView, nint section) => 1;
    public override string? TitleForHeader(UITableView tableView, nint section) => section == 0 ? "Next suggestion" : "Controls";
    public override string? TitleForFooter(UITableView tableView, nint section) => section == 0 && Reviews.Count > 0
        ? $"{Reviews.Count:N0} suggestion{(Reviews.Count == 1 ? string.Empty : "s")} remaining"
        : null;

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 1)
        {
            var text = _lastReview is null
                ? "Right: accept · Left: keep current · Up: ignore"
                : "Undo the last decision";
            var cell = DetailCell("triage-controls", _lastReview is null ? "Swipe the card" : "Undo", text);
            cell.Accessory = _lastReview is null ? UITableViewCellAccessory.None : UITableViewCellAccessory.DisclosureIndicator;
            cell.SelectionStyle = _lastReview is null ? UITableViewCellSelectionStyle.None : UITableViewCellSelectionStyle.Default;
            return cell;
        }
        if (Reviews.Count == 0)
            return DetailCell("triage-empty", "All caught up", "No date suggestions are waiting for a decision.");
        var review = Reviews[0];
        var card = new KnowledgeReviewCardCell();
        card.Configure(
            review,
            () => Resolve(review, 0),
            () => Resolve(review, review.CurrentLibraryDate.HasValue ? 1 : 5),
            () => Resolve(review, 2));
        return card;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Section == 1 && _lastReview is { } last && !_resolving)
        {
            _lastReview = null;
            _ = ResolveAsync(last, 6, isUndo: true);
        }
    }

    private void Resolve(MobileKnowledgeDateReview review, int action)
    {
        if (_resolving) return;
        _lastReview = review;
        _ = ResolveAsync(review, action, isUndo: false);
    }

    private async Task ResolveAsync(MobileKnowledgeDateReview review, int action, bool isUndo)
    {
        _resolving = true;
        var resolved = await Session.ResolveKnowledgeDateReviewAsync(review, action).ConfigureAwait(false);
        BeginInvokeOnMainThread(() =>
        {
            if (!resolved && !isUndo) _lastReview = null;
            TableView.ReloadData();
        });
        _resolving = false;
    }
}

internal sealed class KnowledgeReviewCardCell : UITableViewCell
{
    private readonly UIView _card = new();
    private Action? _accept;
    private Action? _reject;
    private Action? _ignore;

    public KnowledgeReviewCardCell() : base(UITableViewCellStyle.Default, "knowledge-review-card")
    {
        BackgroundColor = UIColor.Clear;
        SelectionStyle = UITableViewCellSelectionStyle.None;
        _card.BackgroundColor = RadioVaultTheme.SurfaceRaised;
        _card.Layer.CornerRadius = 22;
        _card.TranslatesAutoresizingMaskIntoConstraints = false;
        ContentView.AddSubview(_card);
        NSLayoutConstraint.ActivateConstraints([
            _card.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 4),
            _card.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -4),
            _card.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 4),
            _card.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -4)
        ]);
        var pan = new UIPanGestureRecognizer(HandlePan);
        _card.AddGestureRecognizer(pan);
    }

    public void Configure(MobileKnowledgeDateReview review, Action accept, Action reject, Action ignore)
    {
        _accept = accept;
        _reject = reject;
        _ignore = ignore;
        foreach (var view in _card.Subviews) view.RemoveFromSuperview();

        var show = Label(review.ShowName, 13, UIFontWeight.Semibold, RadioVaultTheme.Wiki);
        var title = Label(review.DisplayTitle, 22, UIFontWeight.Bold, RadioVaultTheme.Text, lines: 3);
        var proposed = Label(review.ProposedDateText, 28, UIFontWeight.Bold, RadioVaultTheme.Accent);
        var current = Label(
            review.CurrentLibraryDate is { } date ? $"Current Library date: {date:dd MMM yyyy}" : "Currently undated",
            14,
            UIFontWeight.Regular,
            RadioVaultTheme.MutedText,
            lines: 2);
        var evidence = Label(review.EvidenceText, 13, UIFontWeight.Regular, RadioVaultTheme.MutedText, lines: 3);
        var reason = Label(review.Basis, 13, UIFontWeight.Regular, RadioVaultTheme.SubtleText, lines: 4);

        var yes = ActionButton("Accept", RadioVaultTheme.Completed, accept);
        var no = ActionButton(review.CurrentLibraryDate.HasValue ? "Keep current" : "Leave undated", RadioVaultTheme.Danger, reject);
        var later = ActionButton("Ignore", RadioVaultTheme.Settings, ignore);
        var buttons = new UIStackView([no, later, yes])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Distribution = UIStackViewDistribution.FillEqually,
            Spacing = 8
        };
        var stack = new UIStackView([show, title, proposed, current, evidence, reason, buttons])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 10,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        _card.AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints([
            stack.LeadingAnchor.ConstraintEqualTo(_card.LeadingAnchor, 18),
            stack.TrailingAnchor.ConstraintEqualTo(_card.TrailingAnchor, -18),
            stack.TopAnchor.ConstraintEqualTo(_card.TopAnchor, 18),
            stack.BottomAnchor.ConstraintEqualTo(_card.BottomAnchor, -18),
            buttons.HeightAnchor.ConstraintEqualTo(44)
        ]);
    }

    private void HandlePan(UIPanGestureRecognizer gesture)
    {
        var translation = gesture.TranslationInView(_card);
        if (gesture.State == UIGestureRecognizerState.Changed)
        {
            _card.Transform = CGAffineTransform.MakeTranslation(translation.X, translation.Y * 0.35f);
            return;
        }
        if (gesture.State is not (UIGestureRecognizerState.Ended or UIGestureRecognizerState.Cancelled)) return;
        UIView.Animate(0.18, () => _card.Transform = CGAffineTransform.MakeIdentity());
        if (translation.Y < -60 && Math.Abs(translation.Y) > Math.Abs(translation.X)) _ignore?.Invoke();
        else if (translation.X > 60) _accept?.Invoke();
        else if (translation.X < -60) _reject?.Invoke();
    }

    private static UILabel Label(string? text, nfloat size, UIFontWeight weight, UIColor color, int lines = 1)
        => new()
        {
            Text = string.IsNullOrWhiteSpace(text) ? "No supporting note supplied." : text!,
            Font = UIFont.SystemFontOfSize(size, weight)!,
            TextColor = color,
            Lines = lines,
            AdjustsFontForContentSizeCategory = true
        };

    private static UIButton ActionButton(string title, UIColor color, Action action)
    {
        var button = UIButton.FromType(UIButtonType.System);
        button.SetTitle(title, UIControlState.Normal);
        button.SetTitleColor(color, UIControlState.Normal);
        button.TitleLabel!.Font = UIFont.SystemFontOfSize(13, UIFontWeight.Semibold)!;
        button.BackgroundColor = RadioVaultTheme.Surface;
        button.Layer.CornerRadius = 12;
        button.TouchUpInside += (_, _) => action();
        return button;
    }
}
