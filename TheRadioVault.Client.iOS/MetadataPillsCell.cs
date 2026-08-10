using UIKit;

namespace TheRadioVault.Client.iOS;

internal sealed class MetadataPillsCell : UITableViewCell
{
    private readonly UILabel _heading = new();
    private readonly UIStackView _rows = new()
    {
        Axis = UILayoutConstraintAxis.Vertical,
        Alignment = UIStackViewAlignment.Fill,
        Spacing = 7
    };

    public MetadataPillsCell(string identifier = "metadata-pills") : base(UITableViewCellStyle.Default, identifier)
    {
        BackgroundColor = RadioVaultTheme.Surface;
        SelectionStyle = UITableViewCellSelectionStyle.None;
        _heading.Font = UIFont.SystemFontOfSize(11, UIFontWeight.Bold)!;
        _heading.TextColor = RadioVaultTheme.MutedText;
        var content = new UIStackView([_heading, _rows])
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
            content.TopAnchor.ConstraintEqualTo(ContentView.TopAnchor, 10),
            content.BottomAnchor.ConstraintEqualTo(ContentView.BottomAnchor, -10)
        ]);
    }

    public void Configure(string heading, IReadOnlyList<string> values, UIColor color, Action<string> selected)
    {
        _heading.Text = heading.ToUpperInvariant();
        foreach (var existing in _rows.ArrangedSubviews)
        {
            _rows.RemoveArrangedSubview(existing);
            existing.RemoveFromSuperview();
            existing.Dispose();
        }
        for (var index = 0; index < values.Count; index += 2)
        {
            var row = new UIStackView
            {
                Axis = UILayoutConstraintAxis.Horizontal,
                Alignment = UIStackViewAlignment.Fill,
                Distribution = UIStackViewDistribution.FillEqually,
                Spacing = 7
            };
            row.AddArrangedSubview(Pill(values[index], color, selected));
            if (index + 1 < values.Count) row.AddArrangedSubview(Pill(values[index + 1], color, selected));
            else row.AddArrangedSubview(new UIView());
            _rows.AddArrangedSubview(row);
        }
    }

    private static UIButton Pill(string value, UIColor color, Action<string> selected)
    {
        var button = UIButton.FromType(UIButtonType.System);
        button.SetTitle(value, UIControlState.Normal);
        button.SetTitleColor(color, UIControlState.Normal);
        button.TitleLabel!.Font = UIFont.SystemFontOfSize(12, UIFontWeight.Semibold)!;
        button.TitleLabel.Lines = 1;
        button.TitleLabel.AdjustsFontSizeToFitWidth = true;
        button.TitleLabel.MinimumScaleFactor = 0.72f;
        button.BackgroundColor = color.ColorWithAlpha(0.14f);
        button.Layer.CornerRadius = 16;
        button.Layer.BorderWidth = 1;
        button.Layer.BorderColor = color.ColorWithAlpha(0.7f).CGColor;
        button.HeightAnchor.ConstraintEqualTo(32).Active = true;
        button.AccessibilityLabel = value;
        button.AccessibilityHint = "Open related broadcasts and Explore articles";
        button.TouchUpInside += (_, _) => selected(value);
        return button;
    }
}
