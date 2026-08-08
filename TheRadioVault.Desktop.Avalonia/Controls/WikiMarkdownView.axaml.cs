using System.Text.RegularExpressions;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace TheRadioVault.Desktop.Avalonia.Controls;

public partial class WikiMarkdownView : UserControl
{
    public static readonly StyledProperty<string> MarkdownProperty =
        AvaloniaProperty.Register<WikiMarkdownView, string>(nameof(Markdown), string.Empty);
    public static readonly StyledProperty<ICommand?> NavigateCommandProperty =
        AvaloniaProperty.Register<WikiMarkdownView, ICommand?>(nameof(NavigateCommand));
    public static readonly StyledProperty<IEnumerable<string>?> LinkTargetsProperty =
        AvaloniaProperty.Register<WikiMarkdownView, IEnumerable<string>?>(nameof(LinkTargets));
    public static readonly DirectProperty<WikiMarkdownView, bool> HasContentsProperty =
        AvaloniaProperty.RegisterDirect<WikiMarkdownView, bool>(nameof(HasContents), x => x.HasContents);

    private static readonly Regex Links = new(@"(\[\[[^\]]+\]\]|\[[^\]]+\]\(wiki:[^)]+\)|\*\*[^*]+\*\*|\*[^*]+\*)", RegexOptions.Compiled);
    private bool _hasContents;

    static WikiMarkdownView()
    {
        MarkdownProperty.Changed.AddClassHandler<WikiMarkdownView>((view, _) => view.Render());
        LinkTargetsProperty.Changed.AddClassHandler<WikiMarkdownView>((view, _) => view.Render());
    }

    public WikiMarkdownView()
    {
        InitializeComponent();
        Render();
    }

    public string Markdown { get => GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }
    public ICommand? NavigateCommand { get => GetValue(NavigateCommandProperty); set => SetValue(NavigateCommandProperty, value); }
    public IEnumerable<string>? LinkTargets { get => GetValue(LinkTargetsProperty); set => SetValue(LinkTargetsProperty, value); }
    public bool HasContents
    {
        get => _hasContents;
        private set => SetAndRaise(HasContentsProperty, ref _hasContents, value);
    }

    private void Render()
    {
        if (ArticleHost is null || ContentsHost is null) return;
        ArticleHost.Children.Clear();
        ContentsHost.Children.Clear();
        var markdown = string.IsNullOrWhiteSpace(Markdown) ? "This page has no article text yet." : Markdown;
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var inCode = false;
        var paragraph = new List<string>();
        var headingCount = 0;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            ArticleHost.Children.Add(InlineText(string.Join(" ", paragraph).Trim(), 14, FontWeight.Normal));
            paragraph.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                ArticleHost.Children.Add(new Border
                {
                    Padding = new Thickness(10, 6),
                    CornerRadius = new CornerRadius(6),
                    Background = this.FindResource("RvSurfaceRaisedBrush") as IBrush,
                    Child = new TextBlock { Text = line, FontFamily = new FontFamily("Consolas"), FontSize = 12, TextWrapping = TextWrapping.Wrap }
                });
                continue;
            }
            if (string.IsNullOrWhiteSpace(line)) { FlushParagraph(); continue; }
            var headingLevel = line.StartsWith("### ", StringComparison.Ordinal) ? 3
                : line.StartsWith("## ", StringComparison.Ordinal) ? 2
                : line.StartsWith("# ", StringComparison.Ordinal) ? 1 : 0;
            if (headingLevel > 0)
            {
                FlushParagraph();
                var title = line[(headingLevel + 1)..].Trim();
                var heading = new TextBlock
                {
                    Text = title,
                    FontSize = headingLevel switch { 1 => 28, 2 => 22, _ => 18 },
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, headingLevel == 1 ? 4 : 12, 0, 2)
                };
                ArticleHost.Children.Add(heading);
                if (headingLevel <= 2)
                {
                    headingCount++;
                    var button = new Button
                    {
                        Content = title,
                        Classes = { "ghost" },
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        FontSize = 11,
                        Margin = new Thickness(headingLevel == 2 ? 10 : 0, 0, 0, 0)
                    };
                    button.Click += (_, _) => heading.BringIntoView();
                    ContentsHost.Children.Add(button);
                }
                continue;
            }
            if (line is "---" or "***")
            {
                FlushParagraph();
                ArticleHost.Children.Add(new Border { Height = 1, Background = this.FindResource("RvBorderBrush") as IBrush, Margin = new Thickness(0, 8) });
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                FlushParagraph();
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("22,*") };
                row.Children.Add(new TextBlock { Text = "•", FontSize = 17, Foreground = this.FindResource("RvWikiBrush") as IBrush });
                var content = InlineText(line[2..].Trim(), 14, FontWeight.Normal);
                Grid.SetColumn(content, 1);
                row.Children.Add(content);
                ArticleHost.Children.Add(row);
                continue;
            }
            if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushParagraph();
                ArticleHost.Children.Add(new Border
                {
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    BorderBrush = this.FindResource("RvWikiBrush") as IBrush,
                    Padding = new Thickness(12, 5),
                    Child = InlineText(line[2..], 14, FontWeight.Normal, FontStyle.Italic)
                });
                continue;
            }
            paragraph.Add(line.Trim());
        }
        FlushParagraph();
        HasContents = headingCount > 1;
    }

    private WrapPanel InlineText(string text, double size, FontWeight weight, FontStyle style = FontStyle.Normal)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        var cursor = 0;
        foreach (Match match in Links.Matches(text))
        {
            if (match.Index > cursor) AddAutoLinkedFragments(panel, text[cursor..match.Index], size, weight, style);
            var token = match.Value;
            if (token.StartsWith("[[", StringComparison.Ordinal))
            {
                var inner = token[2..^2];
                var split = inner.Split('|', 2);
                panel.Children.Add(LinkButton(split.Length > 1 ? split[1] : split[0], split[0], size));
            }
            else if (token.StartsWith("[", StringComparison.Ordinal) && token.Contains("](wiki:", StringComparison.Ordinal))
            {
                var end = token.IndexOf("](wiki:", StringComparison.Ordinal);
                panel.Children.Add(LinkButton(token[1..end], token[(end + 2)..^1], size));
            }
            else if (token.StartsWith("**", StringComparison.Ordinal)) AddTextFragments(panel, token[2..^2], size, FontWeight.Bold, style);
            else AddTextFragments(panel, token[1..^1], size, weight, FontStyle.Italic);
            cursor = match.Index + match.Length;
        }
        if (cursor < text.Length) AddAutoLinkedFragments(panel, text[cursor..], size, weight, style);
        return panel;
    }

    private void AddAutoLinkedFragments(WrapPanel panel, string text, double size, FontWeight weight, FontStyle style)
    {
        var targets = (LinkTargets ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x) && x.Trim().Length >= 3)
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(x => x.Length)
            .ToArray();
        var cursor = 0;
        while (cursor < text.Length)
        {
            string? best = null;
            var bestIndex = int.MaxValue;
            foreach (var target in targets)
            {
                var index = text.IndexOf(target, cursor, StringComparison.OrdinalIgnoreCase);
                if (index < 0 || !IsWordBoundary(text, index, target.Length)) continue;
                if (index < bestIndex || index == bestIndex && target.Length > (best?.Length ?? 0))
                {
                    best = target;
                    bestIndex = index;
                }
            }
            if (best is null)
            {
                AddTextFragments(panel, text[cursor..], size, weight, style);
                break;
            }
            if (bestIndex > cursor) AddTextFragments(panel, text[cursor..bestIndex], size, weight, style);
            panel.Children.Add(LinkButton(text.Substring(bestIndex, best.Length), best, size));
            cursor = bestIndex + best.Length;
        }
    }

    private static void AddTextFragments(WrapPanel panel, string text, double size, FontWeight weight, FontStyle style)
    {
        foreach (Match fragment in Regex.Matches(text, @"\S+\s*|\s+"))
            panel.Children.Add(Fragment(fragment.Value, size, weight, style));
    }

    private static bool IsWordBoundary(string text, int index, int length)
    {
        var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
        var after = index + length >= text.Length || !char.IsLetterOrDigit(text[index + length]);
        return before && after;
    }

    private static TextBlock Fragment(string text, double size, FontWeight weight, FontStyle style) => new()
    {
        Text = text,
        FontSize = size,
        FontWeight = weight,
        FontStyle = style,
        TextWrapping = TextWrapping.Wrap,
        LineHeight = 23
    };

    private Button LinkButton(string label, string target, double size)
    {
        var exists = LinkTargetExists(target);
        var button = new Button
        {
            Content = exists ? label : $"{label} ?",
            Classes = { "ghost" },
            FontSize = size,
            Padding = new Thickness(2, 0),
            MinHeight = 0,
            Opacity = exists ? 1 : 0.72
        };
        ToolTip.SetTip(button, exists ? $"Open {label}" : $"No page currently matches {target}; open related search results");
        button.Click += (_, _) =>
        {
            var command = NavigateCommand;
            if (command?.CanExecute(target) == true) command.Execute(target);
        };
        return button;
    }

    private bool LinkTargetExists(string target)
    {
        var normalized = NormalizeTarget(target);
        return (LinkTargets ?? Array.Empty<string>()).Any(x => NormalizeTarget(x) == normalized);
    }

    private static string NormalizeTarget(string value)
    {
        var text = Uri.UnescapeDataString((value ?? string.Empty).Trim());
        if (text.StartsWith("wiki:", StringComparison.OrdinalIgnoreCase)) text = text[5..];
        return string.Join(' ', new string(text.ToLowerInvariant().Select(x => char.IsLetterOrDigit(x) ? x : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
