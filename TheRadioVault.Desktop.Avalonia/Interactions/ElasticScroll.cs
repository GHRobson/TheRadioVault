using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace TheRadioVault.Desktop.Avalonia.Interactions;

/// <summary>
/// Shared elastic overscroll behaviour for every Avalonia ScrollViewer. Normal
/// scrolling, keyboard input, inertia and scroll chaining remain owned by the
/// ScrollViewer. While the pointer wheel is actively pushing beyond an edge,
/// displacement follows the input immediately; once input stops, one bounded
/// spring returns the content to rest. This preserves Alpha 3's direct feel
/// without reintroducing the competing impulse/snap-back jitter.
/// </summary>
public sealed class ElasticScroll : AvaloniaObject
{
    private ElasticScroll() { }

    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ElasticScroll, ScrollViewer, bool>("IsEnabled", defaultValue: false);

    private static readonly ConditionalWeakTable<ScrollViewer, ElasticState> States = new();

    static ElasticScroll()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>(OnIsEnabledChanged);
    }

    public static bool GetIsEnabled(AvaloniaObject element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(AvaloniaObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    private static void OnIsEnabledChanged(ScrollViewer viewer, AvaloniaPropertyChangedEventArgs args)
    {
        var enabled = args.NewValue is true;
        if (enabled)
        {
            var state = States.GetValue(viewer, static owner => new ElasticState(owner));
            state.Attach();
        }
        else if (States.TryGetValue(viewer, out var state))
        {
            state.Detach();
        }
    }

    private sealed class ElasticState
    {
        private const double MaximumOffset = 28d;
        private const double EdgeTolerance = 1.25d;
        private const double MinimumMeaningfulDelta = 0.08d;
        private const double InputGain = 7d;
        private const double MaximumImpulse = 12d;
        private const double SpringStrength = 760d;
        private const double SpringDamping = 50d;
        private const double ReleaseDelaySeconds = 0.012d;

        private readonly ScrollViewer _viewer;
        private readonly DispatcherTimer _timer;
        private TranslateTransform? _transform;
        private double _baselineY;
        private double _visualOffset;
        private double _velocity;
        private long _lastInputTimestamp;
        private long _lastTickTimestamp;
        private bool _attached;
        private bool _isReleasing;

        public ElasticState(ScrollViewer viewer)
        {
            _viewer = viewer;
            _timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            _timer.Tick += OnTick;
        }

        public void Attach()
        {
            if (_attached) return;
            _attached = true;
            _viewer.Loaded += OnLoaded;
            _viewer.Unloaded += OnUnloaded;
            _viewer.PointerWheelChanged += OnPointerWheelChanged;
            EnsureTransform();
        }

        public void Detach()
        {
            if (!_attached) return;
            _attached = false;
            _viewer.Loaded -= OnLoaded;
            _viewer.Unloaded -= OnUnloaded;
            _viewer.PointerWheelChanged -= OnPointerWheelChanged;
            _timer.Stop();
            ResetImmediate();
        }

        private void OnLoaded(object? sender, EventArgs e) => EnsureTransform();

        private void OnUnloaded(object? sender, EventArgs e)
        {
            _timer.Stop();
            ResetImmediate();
        }

        private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
        {
            if (ReducedMotionPreference.IsEnabled) return;
            if (Math.Abs(e.Delta.Y) < MinimumMeaningfulDelta)
            {
                // Trackpads can emit a long tail of tiny inertial deltas after
                // the gesture is visibly finished. Do not let that tail keep
                // postponing the return animation.
                if (Math.Abs(_visualOffset) > 0.01d) BeginRelease();
                return;
            }
            if (e.Source is Visual source)
            {
                var nearest = source as ScrollViewer ?? source.GetVisualAncestors().OfType<ScrollViewer>().FirstOrDefault();
                if (nearest is not null && !ReferenceEquals(nearest, _viewer)) return;
            }

            var maximum = Math.Max(0d, _viewer.Extent.Height - _viewer.Viewport.Height);
            var atTop = _viewer.Offset.Y <= EdgeTolerance;
            var atBottom = maximum <= EdgeTolerance || _viewer.Offset.Y >= maximum - EdgeTolerance;
            var pushingPastTop = e.Delta.Y > 0d && atTop;
            var pushingPastBottom = e.Delta.Y < 0d && atBottom;

            if (!pushingPastTop && !pushingPastBottom)
            {
                if (Math.Abs(_visualOffset) > 0.01d)
                    BeginRelease();
                return;
            }

            EnsureTransform();
            if (_transform is null) return;

            // Direct tracking while meaningful input is active keeps the edge
            // attached to the gesture. Tiny trailing deltas are ignored above,
            // so release begins on the next render frame instead of lingering.
            var resistance = Math.Max(0.16d, 1d - Math.Abs(_visualOffset) / MaximumOffset);
            var impulse = Math.Clamp(e.Delta.Y * InputGain * resistance, -MaximumImpulse, MaximumImpulse);
            _visualOffset = Math.Clamp(_visualOffset + impulse, -MaximumOffset, MaximumOffset);
            _velocity = 0d;
            _isReleasing = false;
            _lastInputTimestamp = Stopwatch.GetTimestamp();
            _transform.Y = _baselineY + _visualOffset;
            StartTimer();
        }

        private void BeginRelease()
        {
            if (Math.Abs(_visualOffset) < 0.01d)
            {
                ResetImmediate();
                _timer.Stop();
                return;
            }

            _isReleasing = true;
            _lastInputTimestamp = 0;
            StartTimer();
        }

        private void StartTimer()
        {
            if (_timer.IsEnabled) return;
            _lastTickTimestamp = Stopwatch.GetTimestamp();
            _timer.Start();
        }

        private void EnsureTransform()
        {
            if (_transform is not null) return;
            if (_viewer.Content is not Control content) return;
            if (content.RenderTransform is TranslateTransform existing)
            {
                _transform = existing;
                _baselineY = existing.Y;
                return;
            }
            if (content.RenderTransform is not null) return;
            _transform = new TranslateTransform();
            _baselineY = 0d;
            content.RenderTransform = _transform;
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_transform is null)
            {
                _timer.Stop();
                return;
            }

            var now = Stopwatch.GetTimestamp();
            var frequency = (double)Stopwatch.Frequency;
            var deltaSeconds = _lastTickTimestamp <= 0
                ? 1d / 60d
                : Math.Clamp((now - _lastTickTimestamp) / frequency, 1d / 240d, 1d / 30d);
            _lastTickTimestamp = now;

            if (!_isReleasing)
            {
                if (_lastInputTimestamp > 0 &&
                    (now - _lastInputTimestamp) / frequency >= ReleaseDelaySeconds)
                {
                    _isReleasing = true;
                    _lastInputTimestamp = 0;
                }
                else
                {
                    // Input is still active. The transform was already updated
                    // synchronously in the wheel handler, so do not add lag here.
                    return;
                }
            }

            var acceleration = -_visualOffset * SpringStrength - _velocity * SpringDamping;
            _velocity += acceleration * deltaSeconds;
            _visualOffset += _velocity * deltaSeconds;

            if (Math.Abs(_visualOffset) < 0.05d && Math.Abs(_velocity) < 0.08d)
            {
                ResetImmediate();
                _timer.Stop();
                return;
            }

            _transform.Y = _baselineY + _visualOffset;
        }

        private void ResetImmediate()
        {
            _visualOffset = 0d;
            _velocity = 0d;
            _lastInputTimestamp = 0;
            _lastTickTimestamp = 0;
            _isReleasing = false;
            if (_transform is not null) _transform.Y = _baselineY;
        }
    }

    private static class ReducedMotionPreference
    {
        private const uint SpiGetClientAreaAnimation = 0x1042;
        private static readonly Lazy<bool> Value = new(ReadPreference);
        public static bool IsEnabled => Value.Value;

        private static bool ReadPreference()
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                return SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var animationsEnabled, 0)
                    && !animationsEnabled;
            }
            catch
            {
                return false;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SystemParametersInfo(
            uint uiAction,
            uint uiParam,
            [MarshalAs(UnmanagedType.Bool)] out bool pvParam,
            uint fWinIni);
    }
}
