using Foundation;
using UIKit;

namespace TheRadioVault.Client.iOS;

internal static class RadioVaultAccessibility
{
    public static UIFont ScaledFont(double size, UIFontWeight weight = UIFontWeight.Regular)
        => UIFontMetrics.DefaultMetrics.GetScaledFont(
            UIFont.SystemFontOfSize((nfloat)size, weight)!);

    public static UIFont ScaledMonospacedDigitFont(double size, UIFontWeight weight = UIFontWeight.Regular)
        => UIFontMetrics.DefaultMetrics.GetScaledFont(
            UIFont.MonospacedDigitSystemFontOfSize((nfloat)size, weight)!);

    public static UIFont ScaledItalicFont(double size)
        => UIFontMetrics.DefaultMetrics.GetScaledFont(
            UIFont.ItalicSystemFontOfSize((nfloat)size)!);

    public static bool ReduceMotion => UIAccessibility.IsReduceMotionEnabled;

    public static void PrepareView(UIView? view)
    {
        if (view is null) return;
        if (view is UILabel label) label.AdjustsFontForContentSizeCategory = true;
        if (view is UIButton button && button.TitleLabel is { } title)
            title.AdjustsFontForContentSizeCategory = true;
        foreach (var child in view.Subviews) PrepareView(child);
    }

    public static void Announce(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        UIAccessibility.PostNotification(
            UIAccessibilityPostNotification.Announcement,
            new NSString(message));
    }
}
