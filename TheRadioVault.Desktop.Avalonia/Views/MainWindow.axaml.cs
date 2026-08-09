using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using TheRadioVault.Presentation.ViewModels;

namespace TheRadioVault.Desktop.Avalonia.Views;

public partial class MainWindow : Window
{
    private bool _closeFlushStarted;
    private bool _closeCommitted;
    private bool _playbackSeekGestureActive;
    private double _pendingPlaybackSeekValue;

    public MainWindow()
    {
        InitializeComponent();

        var useMacWindowControls = OperatingSystem.IsMacOS();
        MacWindowControls.IsVisible = useMacWindowControls;
        WindowsWindowControls.IsVisible = !useMacWindowControls;
        if (useMacWindowControls)
            ShellBrandHeader.Margin = new Thickness(0, 22, 0, 0);

        // Slider thumbs mark pointer events handled. Subscribe on the tunnel route
        // with handledEventsToo so a drag always begins and commits a real seek.
        PlaybackSeekSlider.AddHandler(
            InputElement.PointerPressedEvent,
            PlaybackSeekSlider_OnPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PlaybackSeekSlider.AddHandler(
            InputElement.PointerReleasedEvent,
            PlaybackSeekSlider_OnPointerReleased,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        PlaybackSeekSlider.ValueChanged += PlaybackSeekSlider_OnValueChanged;
        PlaybackSeekSlider.PointerCaptureLost += (_, _) => CommitPlaybackSeek();

        AddHandler(
            ScrollViewer.ScrollChangedEvent,
            PageScrollViewer_OnScrollChanged,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        PageContentControl.PropertyChanged += (_, args) =>
        {
            if (args.Property == ContentControl.ContentProperty)
                PageScrollDivider.IsVisible = false;
        };
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeCommitted && DataContext is MainWindowViewModel viewModel)
        {
            e.Cancel = true;
            base.OnClosing(e);
            if (!_closeFlushStarted)
            {
                _closeFlushStarted = true;
                _ = FlushPlaybackAndCloseAsync(viewModel);
            }
            return;
        }

        base.OnClosing(e);
    }

    private async Task FlushPlaybackAndCloseAsync(MainWindowViewModel viewModel)
    {
        ClosingOverlay.IsVisible = true;
        ClosingOverlay.IsHitTestVisible = true;
        await Task.Yield();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await viewModel.Playback.PauseAndFlushAsync(timeout.Token).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            // Closing still completes after a bounded wait, but the failure is retained
            // in diagnostics rather than silently pretending the final write succeeded.
            TheRadioVault.Services.DiagnosticLog.Write("Avalonia shutdown persistence", exception.Message, exception);
        }
        finally
        {
            _closeCommitted = true;
            Close();
        }
    }


    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            e.Handled = true;
            return;
        }

        BeginMoveDrag(e);
    }

    private void MinimizeButton_OnClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_OnClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        => ToggleMaximizeRestore();

    private void CloseButton_OnClick(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
        => Close();

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void PlaybackSeekSlider_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        _playbackSeekGestureActive = true;
        _pendingPlaybackSeekValue = PlaybackSeekSlider.Value;
        viewModel.Playback.BeginSeek();
    }

    private void PlaybackSeekSlider_OnValueChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_playbackSeekGestureActive)
            _pendingPlaybackSeekValue = e.NewValue;
    }

    private void PlaybackSeekSlider_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
        => CommitPlaybackSeek();

    private void CommitPlaybackSeek()
    {
        if (!_playbackSeekGestureActive) return;
        _playbackSeekGestureActive = false;
        if (DataContext is MainWindowViewModel viewModel)
            viewModel.Playback.SeekTo((long)Math.Round(_pendingPlaybackSeekValue));
    }


    private void PageScrollViewer_OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (e.Source is not ScrollViewer viewer || !viewer.Classes.Contains("page-scroll-root")) return;
        PageScrollDivider.IsVisible = viewer.Offset.Y > 0.5;
    }

    private void MainWindow_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        if (e.Source is Visual visual &&
            (visual is TextBox || visual.GetVisualAncestors().OfType<TextBox>().Any()))
            return;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            switch (e.Key)
            {
                case Key.D:
                    _ = viewModel.NavigateToAsync("dashboard");
                    e.Handled = true;
                    return;
                case Key.L:
                    _ = viewModel.NavigateToAsync("library");
                    e.Handled = true;
                    return;
                case Key.P:
                    _ = viewModel.NavigateToAsync("now-playing");
                    e.Handled = true;
                    return;
                case Key.Q:
                    _ = viewModel.NavigateToAsync("queue");
                    e.Handled = true;
                    return;
                case Key.R:
                    _ = viewModel.NavigateToAsync("research");
                    e.Handled = true;
                    return;
                case Key.M when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    if (viewModel.Moments.CreateCurrentCommand.CanExecute(null))
                        viewModel.Moments.CreateCurrentCommand.Execute(null);
                    e.Handled = true;
                    return;
                case Key.M:
                    _ = viewModel.NavigateToAsync("moments");
                    e.Handled = true;
                    return;
                case Key.OemComma:
                    _ = viewModel.NavigateToAsync("tools");
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key == Key.Space && viewModel.Playback.PlayPauseCommand.CanExecute(null))
        {
            viewModel.Playback.PlayPauseCommand.Execute(null);
            e.Handled = true;
        }
    }
}
