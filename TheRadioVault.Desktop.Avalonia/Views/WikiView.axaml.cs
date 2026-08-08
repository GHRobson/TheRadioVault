using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace TheRadioVault.Desktop.Avalonia.Views;

public sealed partial class WikiView : UserControl
{
    private readonly DispatcherTimer _timelineScrollTimer;
    private readonly Stopwatch _timelineScrollClock = new();
    private double _timelineScrollStart;
    private double _timelineScrollTarget;

    public WikiView()
    {
        InitializeComponent();
        _timelineScrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timelineScrollTimer.Tick += TimelineScrollTimer_OnTick;
    }

    private void TimelineScrollViewer_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not ScrollViewer viewer || Math.Abs(e.Delta.Y) < double.Epsilon) return;
        var maximum = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        _timelineScrollStart = viewer.Offset.Y;
        var baseOffset = _timelineScrollTimer.IsEnabled ? _timelineScrollTarget : _timelineScrollStart;
        _timelineScrollTarget = Math.Clamp(baseOffset - e.Delta.Y * 150, 0, maximum);
        _timelineScrollClock.Restart();
        _timelineScrollTimer.Start();
        e.Handled = true;
    }

    private void TimelineScrollTimer_OnTick(object? sender, EventArgs e)
    {
        const double durationMs = 190;
        var progress = Math.Clamp(_timelineScrollClock.Elapsed.TotalMilliseconds / durationMs, 0, 1);
        var eased = 1 - Math.Pow(1 - progress, 3);
        TimelineScrollViewer.Offset = new Vector(
            TimelineScrollViewer.Offset.X,
            _timelineScrollStart + ((_timelineScrollTarget - _timelineScrollStart) * eased));
        if (progress < 1) return;
        _timelineScrollTimer.Stop();
        _timelineScrollClock.Stop();
    }
}
