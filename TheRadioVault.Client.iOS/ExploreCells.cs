using Foundation;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

internal static class ExploreTypography
{
    public static UIFont Serif(nfloat size, UIFontWeight weight = UIFontWeight.Regular)
        => UIFont.FromName(weight >= UIFontWeight.Semibold ? "NewYork-Bold" : "NewYork-Regular", size)
           ?? RadioVaultAccessibility.ScaledFont(size, weight);
}

internal sealed class ExploreDashboardHeroCell : UITableViewCell
{
    private readonly UIImageView _image = new();
    private readonly UILabel[] _metrics = new UILabel[4];
    private static readonly string[] MetricTitles = ["ARTICLES", "EVENTS", "SOURCES", "IMAGES"];

    public ExploreDashboardHeroCell() : base(UITableViewCellStyle.Default, "explore-dashboard-hero")
    {
        BackgroundColor = RadioVaultTheme.SurfaceRaised;
        SelectionStyle = UITableViewCellSelectionStyle.None;

        _image.ContentMode = UIViewContentMode.ScaleAspectFill;
        _image.ClipsToBounds = true;
        _image.Layer.CornerRadius = 16;
        _image.BackgroundColor = RadioVaultTheme.AccentSubtle;
        var eyebrow = Label("RADIO VAULT ENCYCLOPEDIA", RadioVaultAccessibility.ScaledFont(10, UIFontWeight.Bold), RadioVaultTheme.Wiki);
        var title = Label("Explore the stories behind your archive", ExploreTypography.Serif(30, UIFontWeight.Bold), RadioVaultTheme.Text, 0);
        var introduction = Label(
            "Read about programmes, people and turning points, then follow the evidence back to the broadcasts that preserve them.",
            RadioVaultAccessibility.ScaledFont(15), RadioVaultTheme.MutedText, 0);

        var metricViews = new UIView[4];
        for (var index = 0; index < metricViews.Length; index++)
        {
            var value = Label("0", RadioVaultAccessibility.ScaledFont(20, UIFontWeight.Bold), RadioVaultTheme.Text);
            var caption = Label(MetricTitles[index], RadioVaultAccessibility.ScaledFont(9, UIFontWeight.Semibold), RadioVaultTheme.MutedText);
            caption.TextAlignment = UITextAlignment.Center;
            value.TextAlignment = UITextAlignment.Center;
            _metrics[index] = value;
            var stack = new UIStackView([value, caption])
            {
                Axis = UILayoutConstraintAxis.Vertical,
                Alignment = UIStackViewAlignment.Fill,
                Spacing = 2,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            var card = new UIView { BackgroundColor = RadioVaultTheme.Surface };
            card.Layer.CornerRadius = 11;
            card.AddSubview(stack);
            NSLayoutConstraint.ActivateConstraints([
                stack.LeadingAnchor.ConstraintEqualTo(card.LeadingAnchor, 4),
                stack.TrailingAnchor.ConstraintEqualTo(card.TrailingAnchor, -4),
                stack.CenterYAnchor.ConstraintEqualTo(card.CenterYAnchor)
            ]);
            metricViews[index] = card;
        }
        var metrics = new UIStackView(metricViews)
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Fill,
            Distribution = UIStackViewDistribution.FillEqually,
            Spacing = 7
        };
        var stackContent = new UIStackView([_image, eyebrow, title, introduction, metrics])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 10,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        ContentView.AddSubview(stackContent);
        NSLayoutConstraint.ActivateConstraints([
            _image.HeightAnchor.ConstraintEqualTo(178),
            metrics.HeightAnchor.ConstraintEqualTo(66),
            stackContent.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 16),
            stackContent.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -16),
            stackContent.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 16),
            stackContent.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -16)
        ]);
    }

    public void Configure(MobileWikiOverview overview, MobileExploreImage? image)
    {
        _image.Hidden = image is null;
        _image.Image = image is null ? null : UIImage.LoadFromData(NSData.FromArray(image.Content));
        _image.AccessibilityLabel = image?.AltText;
        _metrics[0].Text = overview.PageCount.ToString("N0");
        _metrics[1].Text = overview.TimelineEventCount.ToString("N0");
        _metrics[2].Text = overview.SourceCount.ToString("N0");
        _metrics[3].Text = overview.ImageCount.ToString("N0");
        AccessibilityLabel = $"Explore. {overview.PageCount} articles, {overview.TimelineEventCount} events, {overview.SourceCount} sources, {overview.ImageCount} images.";
    }

    private static UILabel Label(string text, UIFont font, UIColor color, nint lines = 1)
        => new()
        {
            Text = text,
            Font = font,
            TextColor = color,
            Lines = lines,
            AdjustsFontForContentSizeCategory = true
        };
}

internal sealed class ExplorePageCardCell : UITableViewCell
{
    private readonly UIImageView _image = new();
    private readonly UILabel _type = new();
    private readonly UILabel _title = new();
    private readonly UILabel _summary = new();
    private readonly UILabel _evidence = new();

    public ExplorePageCardCell(string identifier = "explore-page-card") : base(UITableViewCellStyle.Default, identifier)
    {
        BackgroundColor = RadioVaultTheme.Surface;
        SelectionStyle = UITableViewCellSelectionStyle.Default;
        Accessory = UITableViewCellAccessory.DisclosureIndicator;
        _image.ContentMode = UIViewContentMode.ScaleAspectFill;
        _image.ClipsToBounds = true;
        _image.Layer.CornerRadius = 11;
        _image.BackgroundColor = RadioVaultTheme.AccentSubtle;
        _type.Font = RadioVaultAccessibility.ScaledFont(9, UIFontWeight.Bold);
        _type.TextColor = RadioVaultTheme.Wiki;
        _title.Font = ExploreTypography.Serif(21, UIFontWeight.Bold);
        _title.TextColor = RadioVaultTheme.Text;
        _title.Lines = 2;
        _summary.Font = RadioVaultAccessibility.ScaledFont(13);
        _summary.TextColor = RadioVaultTheme.MutedText;
        _summary.Lines = 3;
        _evidence.Font = RadioVaultAccessibility.ScaledFont(10, UIFontWeight.Medium);
        _evidence.TextColor = RadioVaultTheme.SubtleText;
        var labels = new UIStackView([_type, _title, _summary, _evidence])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 4
        };
        var content = new UIStackView([_image, labels])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Top,
            Distribution = UIStackViewDistribution.Fill,
            Spacing = 13,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        ContentView.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints([
            _image.WidthAnchor.ConstraintEqualTo(82),
            _image.HeightAnchor.ConstraintEqualTo(82),
            content.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 14),
            content.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -8),
            content.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 13),
            content.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -13)
        ]);
    }

    public void Configure(MobileWikiPageSummary page, MobileExploreImage? image = null)
    {
        _type.Text = page.PageType.ToUpperInvariant();
        _title.Text = page.Title;
        _summary.Text = string.IsNullOrWhiteSpace(page.Summary) ? "Open this article to read more." : page.Summary;
        _evidence.Text = page.EvidenceSummary;
        _image.Image = image is null
            ? RadioVaultIcons.Image(RadioVaultIcon.Explore, RadioVaultTheme.Wiki, 42, 1.7)
            : UIImage.LoadFromData(NSData.FromArray(image.Content));
        _image.ContentMode = image is null ? UIViewContentMode.Center : UIViewContentMode.ScaleAspectFill;
        _image.AccessibilityLabel = image?.AltText;
        AccessibilityLabel = $"{page.Title}. {page.Summary}. {page.EvidenceSummary}";
    }
}

internal sealed class ExploreTimelinePromoCell : UITableViewCell
{
    public ExploreTimelinePromoCell(int showCount, int eventCount) : base(UITableViewCellStyle.Default, "explore-timeline-promo")
    {
        BackgroundColor = RadioVaultTheme.SurfaceRaised;
        Accessory = UITableViewCellAccessory.DisclosureIndicator;
        var icon = new UIImageView
        {
            Image = RadioVaultIcons.Image(RadioVaultIcon.Radio, RadioVaultTheme.Wiki, 42, 2),
            ContentMode = UIViewContentMode.Center
        };
        var eyebrow = new UILabel
        {
            Text = "INTERACTIVE HISTORY",
            Font = RadioVaultAccessibility.ScaledFont(9, UIFontWeight.Bold),
            TextColor = RadioVaultTheme.Wiki
        };
        var title = new UILabel
        {
            Text = "Show timelines",
            Font = ExploreTypography.Serif(24, UIFontWeight.Bold),
            TextColor = RadioVaultTheme.Text
        };
        var detail = new UILabel
        {
            Text = $"Travel through {eventCount:N0} dated events across {showCount:N0} shows.",
            Font = RadioVaultAccessibility.ScaledFont(13),
            TextColor = RadioVaultTheme.MutedText,
            Lines = 0
        };
        var labels = new UIStackView([eyebrow, title, detail])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 4
        };
        var content = new UIStackView([icon, labels])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 13,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        ContentView.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints([
            icon.WidthAnchor.ConstraintEqualTo(54),
            content.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 14),
            content.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -8),
            content.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 16),
            content.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -16)
        ]);
        AccessibilityLabel = $"Show timelines, {eventCount} dated events across {showCount} shows";
    }
}

internal sealed class ExploreArticleHeaderCell : UITableViewCell
{
    private readonly UILabel _type = new();
    private readonly UILabel _title = new();
    private readonly UILabel _summary = new();
    private readonly UILabel _byline = new();

    public ExploreArticleHeaderCell() : base(UITableViewCellStyle.Default, "explore-article-header")
    {
        BackgroundColor = RadioVaultTheme.Surface;
        SelectionStyle = UITableViewCellSelectionStyle.None;
        _type.Font = RadioVaultAccessibility.ScaledFont(10, UIFontWeight.Bold);
        _type.TextColor = RadioVaultTheme.Wiki;
        _title.Font = ExploreTypography.Serif(34, UIFontWeight.Bold);
        _title.TextColor = RadioVaultTheme.Text;
        _title.Lines = 0;
        _summary.Font = ExploreTypography.Serif(18);
        _summary.TextColor = RadioVaultTheme.Text;
        _summary.Lines = 0;
        _byline.Font = RadioVaultAccessibility.ScaledFont(11);
        _byline.TextColor = RadioVaultTheme.SubtleText;
        _byline.Lines = 0;
        var rule = new UIView { BackgroundColor = RadioVaultTheme.Border };
        var content = new UIStackView([_type, _title, rule, _summary, _byline])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 9,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        ContentView.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints([
            rule.HeightAnchor.ConstraintEqualTo(1),
            content.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 16),
            content.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -16),
            content.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 18),
            content.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -18)
        ]);
    }

    public void Configure(MobileWikiPageSummary summary, MobileWikiPageDocument? document, bool loading, string status)
    {
        _type.Text = (document?.PageType ?? summary.PageType).ToUpperInvariant();
        _title.Text = document?.Title ?? summary.Title;
        _summary.Text = document is null
            ? loading ? "Loading article…" : status
            : string.IsNullOrWhiteSpace(document.Summary) ? "This article is awaiting an introduction." : document.Summary;
        _byline.Text = document is null
            ? string.Empty
            : $"{document.Status} · revision {document.Revision:N0} · updated {document.UpdatedAt:dd MMMM yyyy} · {document.LastEditor}";
    }
}

internal sealed class ExploreArticleBodyCell : UITableViewCell
{
    private static readonly System.Text.RegularExpressions.Regex InlineTokens = new(
        @"\[\[(?<target>[^\]|]+)(?:\|(?<label>[^\]]+))?\]\]|\[(?<label2>[^\]]+)\]\(wiki:(?<target2>[^)]+)\)|\*\*(?<bold>[^*]+)\*\*|\*(?<italic>[^*]+)\*",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private readonly IReadOnlyList<string> _linkTargets;
    private readonly Action<string>? _navigate;
    private readonly List<UITextViewDelegate> _linkDelegates = [];

    public ExploreArticleBodyCell(
        string? markdown,
        IReadOnlyList<string>? linkTargets = null,
        Action<string>? navigate = null) : base(UITableViewCellStyle.Default, "explore-article-body")
    {
        _linkTargets = linkTargets ?? [];
        _navigate = navigate;
        BackgroundColor = RadioVaultTheme.Surface;
        SelectionStyle = UITableViewCellSelectionStyle.None;
        var content = new UIStackView
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 11,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        foreach (var block in Blocks(markdown)) content.AddArrangedSubview(block);
        ContentView.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints([
            content.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 16),
            content.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -16),
            content.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 14),
            content.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -18)
        ]);
    }

    private IEnumerable<UIView> Blocks(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            yield return Paragraph("This article does not have any body text yet.", true);
            yield break;
        }
        var paragraph = new List<string>();
        foreach (var raw in markdown.Replace("\r", string.Empty).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (paragraph.Count > 0)
                {
                    yield return Paragraph(string.Join(" ", paragraph), false);
                    paragraph.Clear();
                }
                continue;
            }
            if (line.StartsWith('#'))
            {
                if (paragraph.Count > 0)
                {
                    yield return Paragraph(string.Join(" ", paragraph), false);
                    paragraph.Clear();
                }
                var level = line.TakeWhile(value => value == '#').Count();
                yield return Heading(Clean(line[level..].Trim()), level);
            }
            else if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                if (paragraph.Count > 0)
                {
                    yield return Paragraph(string.Join(" ", paragraph), false);
                    paragraph.Clear();
                }
                yield return Paragraph("•  " + line[2..].Trim(), false);
            }
            else paragraph.Add(line);
        }
        if (paragraph.Count > 0) yield return Paragraph(string.Join(" ", paragraph), false);
    }

    private static UILabel Heading(string text, int level)
        => new()
        {
            Text = text,
            Font = ExploreTypography.Serif(level <= 2 ? 25 : 20, UIFontWeight.Bold),
            TextColor = RadioVaultTheme.Text,
            Lines = 0,
            AdjustsFontForContentSizeCategory = true
        };

    private UIView Paragraph(string text, bool muted)
    {
        var view = new UITextView
        {
            BackgroundColor = UIColor.Clear,
            Editable = false,
            Selectable = true,
            ScrollEnabled = false,
            TextContainerInset = UIEdgeInsets.Zero,
            DataDetectorTypes = UIDataDetectorType.None
        };
        view.TextContainer.LineFragmentPadding = 0;
        view.AttributedText = RenderInline(text, muted);
        view.WeakLinkTextAttributes = new UIStringAttributes
        {
            ForegroundColor = RadioVaultTheme.Wiki,
            UnderlineStyle = NSUnderlineStyle.Single
        }.Dictionary;
        var linkDelegate = new ExploreLinkTextViewDelegate(target => _navigate?.Invoke(target));
        _linkDelegates.Add(linkDelegate);
        view.Delegate = linkDelegate;
        return view;
    }

    private NSAttributedString RenderInline(string source, bool muted)
    {
        var text = new System.Text.StringBuilder();
        var links = new List<(int Start, int Length, string Target)>();
        var bold = new List<(int Start, int Length)>();
        var italic = new List<(int Start, int Length)>();
        var cursor = 0;
        foreach (System.Text.RegularExpressions.Match match in InlineTokens.Matches(source))
        {
            if (match.Index > cursor) text.Append(source[cursor..match.Index]);
            var start = text.Length;
            if (match.Groups["target"].Success)
            {
                var label = match.Groups["label"].Success ? match.Groups["label"].Value : match.Groups["target"].Value;
                text.Append(label);
                links.Add((start, label.Length, match.Groups["target"].Value));
            }
            else if (match.Groups["target2"].Success)
            {
                var label = match.Groups["label2"].Value;
                text.Append(label);
                links.Add((start, label.Length, match.Groups["target2"].Value));
            }
            else if (match.Groups["bold"].Success)
            {
                var value = match.Groups["bold"].Value;
                text.Append(value);
                bold.Add((start, value.Length));
            }
            else
            {
                var value = match.Groups["italic"].Value;
                text.Append(value);
                italic.Add((start, value.Length));
            }
            cursor = match.Index + match.Length;
        }
        if (cursor < source.Length) text.Append(source[cursor..]);

        var plain = text.ToString();
        foreach (var target in _linkTargets)
        {
            var searchFrom = 0;
            while (searchFrom < plain.Length)
            {
                var index = plain.IndexOf(target, searchFrom, StringComparison.CurrentCultureIgnoreCase);
                if (index < 0) break;
                var overlaps = links.Any(link => index < link.Start + link.Length && index + target.Length > link.Start);
                if (!overlaps && IsWordBoundary(plain, index, target.Length)) links.Add((index, target.Length, target));
                searchFrom = index + target.Length;
            }
        }

        var result = new NSMutableAttributedString(plain);
        var full = new NSRange(0, plain.Length);
        result.AddAttribute(UIStringAttributeKey.Font, ExploreTypography.Serif(17), full);
        result.AddAttribute(UIStringAttributeKey.ForegroundColor, muted ? RadioVaultTheme.MutedText : RadioVaultTheme.Text, full);
        foreach (var range in bold)
            result.AddAttribute(UIStringAttributeKey.Font, ExploreTypography.Serif(17, UIFontWeight.Bold), new NSRange(range.Start, range.Length));
        foreach (var range in italic)
            result.AddAttribute(UIStringAttributeKey.Obliqueness, NSNumber.FromFloat(0.18f), new NSRange(range.Start, range.Length));
        foreach (var link in links)
            result.AddAttribute(
                UIStringAttributeKey.Link,
                new NSUrl($"radiovault://link/{Uri.EscapeDataString(link.Target)}"),
                new NSRange(link.Start, link.Length));
        return result;
    }

    private static bool IsWordBoundary(string text, int index, int length)
    {
        var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
        var after = index + length >= text.Length || !char.IsLetterOrDigit(text[index + length]);
        return before && after;
    }

    private static string Clean(string value)
        => System.Text.RegularExpressions.Regex.Replace(
                value.Replace("**", string.Empty).Replace("__", string.Empty).Replace("`", string.Empty),
                @"\[([^\]]+)\]\([^\)]+\)",
                "$1")
            .Trim();

    private sealed class ExploreLinkTextViewDelegate(Action<string> selected) : UITextViewDelegate
    {
        public override bool ShouldInteractWithUrl(UITextView textView, NSUrl url, NSRange characterRange)
        {
            const string prefix = "radiovault://link/";
            var absolute = url.AbsoluteString ?? string.Empty;
            if (!absolute.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
            selected(Uri.UnescapeDataString(absolute[prefix.Length..]));
            return false;
        }
    }
}

internal sealed class ExploreTimelineEventCell : UITableViewCell
{
    private readonly UILabel _year = new();
    private readonly UILabel _date = new();
    private readonly UILabel _title = new();
    private readonly UILabel _summary = new();
    private readonly UILabel _evidence = new();

    public ExploreTimelineEventCell() : base(UITableViewCellStyle.Default, "explore-timeline-event")
    {
        BackgroundColor = RadioVaultTheme.Background;
        SelectionStyle = UITableViewCellSelectionStyle.Default;
        var line = new UIView { BackgroundColor = RadioVaultTheme.Wiki, TranslatesAutoresizingMaskIntoConstraints = false };
        var dot = new UIView { BackgroundColor = RadioVaultTheme.Wiki, TranslatesAutoresizingMaskIntoConstraints = false };
        dot.Layer.CornerRadius = 6;
        _year.Font = RadioVaultAccessibility.ScaledFont(25, UIFontWeight.Bold);
        _year.TextColor = RadioVaultTheme.Wiki;
        _date.Font = RadioVaultAccessibility.ScaledFont(9, UIFontWeight.Bold);
        _date.TextColor = RadioVaultTheme.MutedText;
        _title.Font = ExploreTypography.Serif(21, UIFontWeight.Bold);
        _title.TextColor = RadioVaultTheme.Text;
        _title.Lines = 0;
        _summary.Font = RadioVaultAccessibility.ScaledFont(14);
        _summary.TextColor = RadioVaultTheme.MutedText;
        _summary.Lines = 0;
        _evidence.Font = RadioVaultAccessibility.ScaledFont(10);
        _evidence.TextColor = RadioVaultTheme.SubtleText;
        var content = new UIStackView([_year, _date, _title, _summary, _evidence])
        {
            Axis = UILayoutConstraintAxis.Vertical,
            Alignment = UIStackViewAlignment.Fill,
            Spacing = 5,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        ContentView.AddSubview(line);
        ContentView.AddSubview(dot);
        ContentView.AddSubview(content);
        NSLayoutConstraint.ActivateConstraints([
            line.LeadingAnchor.ConstraintEqualTo(ContentView.LeadingAnchor, 19),
            line.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor),
            line.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor),
            line.WidthAnchor.ConstraintEqualTo(2),
            dot.CenterXAnchor.ConstraintEqualTo(line.CenterXAnchor),
            dot.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 28),
            dot.WidthAnchor.ConstraintEqualTo(12),
            dot.HeightAnchor.ConstraintEqualTo(12),
            content.LeadingAnchor.ConstraintEqualTo(line.TrailingAnchor, 20),
            content.TrailingAnchor.ConstraintEqualTo(ContentView.TrailingAnchor, -14),
            content.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 18),
            content.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -20)
        ]);
    }

    public void Configure(MobileWikiTimelineEvent item)
    {
        _year.Text = item.YearText;
        _date.Text = string.IsNullOrWhiteSpace(item.DateDisplay) ? item.Category.ToUpperInvariant() : item.DateDisplay.ToUpperInvariant();
        _title.Text = item.Title;
        _summary.Text = item.Summary;
        _summary.Hidden = string.IsNullOrWhiteSpace(item.Summary);
        _evidence.Text = item.EvidenceSummary;
        Accessory = (item.Broadcasts?.Count ?? 0) > 0
            ? UITableViewCellAccessory.DisclosureIndicator
            : UITableViewCellAccessory.None;
        AccessibilityLabel = $"{item.YearText}, {item.Title}. {item.Summary}. {item.EvidenceSummary}";
    }
}
