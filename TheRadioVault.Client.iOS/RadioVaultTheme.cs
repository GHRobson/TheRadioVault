using CoreGraphics;
using UIKit;

namespace TheRadioVault.Client.iOS;

public static class RadioVaultTheme
{
    public static UIColor Background { get; } = Color(0x11, 0x13, 0x17);
    public static UIColor Shell { get; } = Color(0x15, 0x18, 0x1D);
    public static UIColor Surface { get; } = Color(0x1C, 0x20, 0x26);
    public static UIColor SurfaceRaised { get; } = Color(0x23, 0x28, 0x2F);
    public static UIColor SurfaceHover { get; } = Color(0x2B, 0x31, 0x39);
    public static UIColor Border { get; } = Color(0x36, 0x3D, 0x46);
    public static UIColor Text { get; } = Color(0xF5, 0xF6, 0xF8);
    public static UIColor MutedText { get; } = Color(0xAF, 0xB5, 0xBD);
    public static UIColor SubtleText { get; } = Color(0x7F, 0x87, 0x91);
    public static UIColor Accent { get; } = Color(0xF2, 0xC9, 0x4C);
    public static UIColor AccentSubtle { get; } = Color(0x3A, 0x33, 0x1A);
    public static UIColor Progress { get; } = Color(0x68, 0xB5, 0xFF);
    public static UIColor Completed { get; } = Color(0x52, 0xD6, 0xA2);
    public static UIColor Favourite { get; } = Color(0xF0, 0x8D, 0xB7);
    public static UIColor Wiki { get; } = Color(0x8E, 0xA7, 0xFF);
    public static UIColor Settings { get; } = Color(0xA9, 0xB0, 0xB8);
    public static UIColor Danger { get; } = Color(0xF8, 0x71, 0x82);

    public static void ApplyGlobalAppearance()
    {
        var navigation = new UINavigationBarAppearance();
        navigation.ConfigureWithOpaqueBackground();
        navigation.BackgroundColor = Shell;
        navigation.ShadowColor = Border;
        navigation.TitleTextAttributes = new UIStringAttributes { ForegroundColor = Text };
        navigation.LargeTitleTextAttributes = new UIStringAttributes { ForegroundColor = Text };
        UINavigationBar.Appearance.StandardAppearance = navigation;
        UINavigationBar.Appearance.ScrollEdgeAppearance = navigation;
        UINavigationBar.Appearance.CompactAppearance = navigation;
        UINavigationBar.Appearance.TintColor = Accent;

        var tabs = new UITabBarAppearance();
        tabs.ConfigureWithOpaqueBackground();
        tabs.BackgroundColor = Shell;
        tabs.ShadowColor = Border;
        tabs.StackedLayoutAppearance.Normal.TitleTextAttributes =
            new UIStringAttributes { ForegroundColor = MutedText };
        tabs.StackedLayoutAppearance.Selected.TitleTextAttributes =
            new UIStringAttributes { ForegroundColor = Accent };
        UITabBar.Appearance.StandardAppearance = tabs;
        UITabBar.Appearance.ScrollEdgeAppearance = tabs;
        UITabBar.Appearance.TintColor = Accent;
        UITabBar.Appearance.UnselectedItemTintColor = MutedText;

        UITableView.Appearance.BackgroundColor = Background;
        UITableViewCell.Appearance.BackgroundColor = Surface;
        UITableViewCell.Appearance.TintColor = Accent;
        UISwitch.Appearance.OnTintColor = Accent;
        UISlider.Appearance.MinimumTrackTintColor = Accent;
        UIProgressView.Appearance.ProgressTintColor = Accent;
        UIRefreshControl.Appearance.TintColor = Accent;
    }

    public static void StyleCell(UITableViewCell cell, UIListContentConfiguration content)
    {
        cell.BackgroundColor = Surface;
        cell.TintColor = Accent;
        content.TextProperties.Color = Text;
        content.SecondaryTextProperties.Color = MutedText;
        cell.ContentConfiguration = content;
    }

    private static UIColor Color(byte red, byte green, byte blue)
        => UIColor.FromRGB(red, green, blue);
}

public enum RadioVaultIcon
{
    Home,
    Search,
    Library,
    Favourite,
    Play,
    Pause,
    Knowledge,
    Download,
    Settings,
    Completed,
    UpNext,
    Radio,
    Handoff
}

public static class RadioVaultIcons
{
    public static UIImage Image(RadioVaultIcon icon, UIColor? color = null, double size = 24, double strokeWidth = 1.9)
    {
        color ??= ColorFor(icon);
        using var renderer = new UIGraphicsImageRenderer(new CGSize(size, size));
        var image = renderer.CreateImage(rendererContext =>
        {
            var context = rendererContext.CGContext;
            var scale = size / 24d;
            context.ScaleCTM((nfloat)scale, (nfloat)scale);
            context.SetStrokeColor(color.CGColor);
            context.SetFillColor(color.CGColor);
            context.SetLineWidth((nfloat)strokeWidth);
            context.SetLineCap(CGLineCap.Round);
            context.SetLineJoin(CGLineJoin.Round);

            Draw(context, icon);
        });
        return image.ImageWithRenderingMode(UIImageRenderingMode.AlwaysOriginal);
    }

    private static UIColor ColorFor(RadioVaultIcon icon) => icon switch
    {
        RadioVaultIcon.Play or RadioVaultIcon.Pause or RadioVaultIcon.UpNext or RadioVaultIcon.Handoff => RadioVaultTheme.Progress,
        RadioVaultIcon.Favourite => RadioVaultTheme.Favourite,
        RadioVaultIcon.Knowledge => RadioVaultTheme.Wiki,
        RadioVaultIcon.Download or RadioVaultIcon.Settings => RadioVaultTheme.Settings,
        RadioVaultIcon.Completed => RadioVaultTheme.Completed,
        RadioVaultIcon.Search => Color(0x78, 0xB6, 0xD8),
        _ => RadioVaultTheme.Accent
    };

    private static UIColor Color(byte red, byte green, byte blue) => UIColor.FromRGB(red, green, blue);

    private static void Draw(CGContext context, RadioVaultIcon icon)
    {
        switch (icon)
        {
            case RadioVaultIcon.Home:
                Lines(context, (4, 12), (12, 5), (20, 12));
                Lines(context, (6.5, 10.5), (6.5, 20), (17.5, 20), (17.5, 10.5));
                Lines(context, (10, 20), (10, 14), (14, 14), (14, 20));
                break;
            case RadioVaultIcon.Search:
                context.StrokeEllipseInRect(new CGRect(4, 4, 13, 13));
                Lines(context, (15.2, 15.2), (20, 20));
                break;
            case RadioVaultIcon.Library:
                Polygon(context, false, (5, 3), (19, 3), (19, 21), (5, 21));
                Lines(context, (9, 8), (15, 8));
                Lines(context, (9, 12), (15, 12));
                Lines(context, (9, 16), (15, 16));
                break;
            case RadioVaultIcon.Favourite:
                context.MoveTo(12, 20);
                context.AddLineToPoint(5.2f, 13.8f);
                context.AddCurveToPoint(2, 10.8f, 3.7f, 6, 7.5f, 6);
                context.AddCurveToPoint(9.4f, 6, 11, 7.1f, 12, 8.7f);
                context.AddCurveToPoint(13, 7.1f, 14.6f, 6, 16.5f, 6);
                context.AddCurveToPoint(20.3f, 6, 22, 10.8f, 18.8f, 13.8f);
                context.ClosePath();
                context.StrokePath();
                break;
            case RadioVaultIcon.Play:
                Polygon(context, false, (8, 5), (18, 12), (8, 19));
                break;
            case RadioVaultIcon.Pause:
                Lines(context, (9, 6), (9, 18));
                Lines(context, (15, 6), (15, 18));
                break;
            case RadioVaultIcon.Knowledge:
                context.MoveTo(4, 5);
                context.AddCurveToPoint(7, 4, 9.5f, 4.5f, 12, 6);
                context.AddLineToPoint(12, 20);
                context.AddCurveToPoint(9.5f, 18.5f, 7, 18, 4, 19);
                context.ClosePath();
                context.MoveTo(20, 5);
                context.AddCurveToPoint(17, 4, 14.5f, 4.5f, 12, 6);
                context.AddLineToPoint(12, 20);
                context.AddCurveToPoint(14.5f, 18.5f, 17, 18, 20, 19);
                context.ClosePath();
                context.StrokePath();
                Lines(context, (7, 9), (10, 9));
                Lines(context, (14, 9), (17, 9));
                Lines(context, (7, 12), (10, 12));
                Lines(context, (14, 12), (17, 12));
                break;
            case RadioVaultIcon.Download:
                Lines(context, (12, 3), (12, 15));
                Lines(context, (8, 11), (12, 15), (16, 11));
                Lines(context, (5, 20), (19, 20));
                break;
            case RadioVaultIcon.Settings:
                Lines(context, (4, 7), (20, 7));
                Lines(context, (4, 12), (20, 12));
                Lines(context, (4, 17), (20, 17));
                Lines(context, (8, 4), (8, 10));
                Lines(context, (16, 9), (16, 15));
                Lines(context, (10, 14), (10, 20));
                break;
            case RadioVaultIcon.Completed:
                Lines(context, (3, 13), (9, 19), (21, 6));
                break;
            case RadioVaultIcon.UpNext:
                Lines(context, (4, 7), (15, 7));
                Lines(context, (4, 12), (15, 12));
                Lines(context, (4, 17), (11, 17));
                Polygon(context, false, (17, 14), (21, 17), (17, 20));
                break;
            case RadioVaultIcon.Radio:
                context.StrokeEllipseInRect(new CGRect(7.5, 7.5, 9, 9));
                context.StrokeEllipseInRect(new CGRect(10, 10, 4, 4));
                Lines(context, (12, 16.5), (12, 22));
                context.MoveTo(6.5f, 6);
                context.AddCurveToPoint(3.5f, 9.1f, 3.5f, 14.9f, 6.5f, 18);
                context.StrokePath();
                context.MoveTo(17.5f, 6);
                context.AddCurveToPoint(20.5f, 9.1f, 20.5f, 14.9f, 17.5f, 18);
                context.StrokePath();
                break;
            case RadioVaultIcon.Handoff:
                context.MoveTo(10, 3);
                context.AddLineToPoint(17, 3);
                context.AddArcToPoint(19, 3, 19, 5, 2);
                context.AddLineToPoint(19, 19);
                context.AddArcToPoint(19, 21, 17, 21, 2);
                context.AddLineToPoint(10, 21);
                context.AddArcToPoint(8, 21, 8, 19, 2);
                context.AddLineToPoint(8, 5);
                context.AddArcToPoint(8, 3, 10, 3, 2);
                context.ClosePath();
                context.StrokePath();
                Lines(context, (11, 18), (16, 18));
                Lines(context, (3, 10.5), (13, 10.5));
                Lines(context, (9, 6.5), (13, 10.5), (9, 14.5));
                break;
        }
    }

    private static void Lines(CGContext context, params (double X, double Y)[] points)
    {
        if (points.Length < 2) return;
        context.MoveTo((nfloat)points[0].X, (nfloat)points[0].Y);
        foreach (var point in points.Skip(1))
            context.AddLineToPoint((nfloat)point.X, (nfloat)point.Y);
        context.StrokePath();
    }

    private static void Polygon(CGContext context, bool fill, params (double X, double Y)[] points)
    {
        if (points.Length < 2) return;
        context.MoveTo((nfloat)points[0].X, (nfloat)points[0].Y);
        foreach (var point in points.Skip(1))
            context.AddLineToPoint((nfloat)point.X, (nfloat)point.Y);
        context.ClosePath();
        if (fill) context.FillPath(); else context.StrokePath();
    }
}
