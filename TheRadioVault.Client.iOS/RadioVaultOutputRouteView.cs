using AVFoundation;
using AVKit;
using Foundation;
using UIKit;

namespace TheRadioVault.Client.iOS;

internal sealed class RadioVaultOutputRouteView : UIView
{
    private readonly UILabel _label = new();
    private readonly AVRoutePickerView _picker = new();
    private readonly NSObject _routeObserver;

    public RadioVaultOutputRouteView()
    {
        TranslatesAutoresizingMaskIntoConstraints = false;
        BackgroundColor = RadioVaultTheme.SurfaceRaised;
        Layer.CornerRadius = 16;

        _label.Font = RadioVaultAccessibility.ScaledFont(12, UIFontWeight.Semibold);
        _label.TextColor = RadioVaultTheme.MutedText;
        _label.Lines = 1;
        _label.LineBreakMode = UILineBreakMode.TailTruncation;
        _label.AdjustsFontForContentSizeCategory = true;

        _picker.TintColor = RadioVaultTheme.MutedText;
        _picker.ActiveTintColor = RadioVaultTheme.Accent;
        _picker.PrioritizesVideoDevices = false;
        _picker.TranslatesAutoresizingMaskIntoConstraints = false;
        _picker.IsAccessibilityElement = true;
        _picker.AccessibilityLabel = "Choose audio output";
        _picker.AccessibilityHint = "Choose this iPhone, AirPlay, Bluetooth or another available speaker";
        _picker.AccessibilityTraits = UIAccessibilityTrait.Button;

        var stack = new UIStackView([_label, _picker])
        {
            Axis = UILayoutConstraintAxis.Horizontal,
            Alignment = UIStackViewAlignment.Center,
            Spacing = 8,
            TranslatesAutoresizingMaskIntoConstraints = false
        };
        AddSubview(stack);
        NSLayoutConstraint.ActivateConstraints([
            stack.LeadingAnchor.ConstraintEqualTo(LeadingAnchor, 12),
            stack.TrailingAnchor.ConstraintEqualTo(TrailingAnchor, -8),
            stack.TopAnchor.ConstraintEqualTo(TopAnchor, 4),
            stack.BottomAnchor.ConstraintEqualTo(BottomAnchor, -4),
            _picker.WidthAnchor.ConstraintEqualTo(36),
            _picker.HeightAnchor.ConstraintEqualTo(36)
        ]);

        _routeObserver = AVAudioSession.Notifications.ObserveRouteChange((_, _) =>
            BeginInvokeOnMainThread(UpdateRoute));
        UpdateRoute();
    }

    private void UpdateRoute()
    {
        var outputs = AVAudioSession.SharedInstance().CurrentRoute.Outputs;
        var output = outputs.Length == 0
            ? "This iPhone"
            : string.Join(", ", outputs.Select(value => value.PortName));
        _label.Text = output;
        _picker.AccessibilityValue = $"Current output: {output}";
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _routeObserver.Dispose();
        base.Dispose(disposing);
    }
}
