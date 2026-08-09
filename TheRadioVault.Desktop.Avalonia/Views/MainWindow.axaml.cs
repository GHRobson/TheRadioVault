using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Diagnostics;
using TheRadioVault.Desktop.Avalonia.Platform;
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

    private void NavigateMenuItem_OnClick(object? sender, EventArgs e)
    {
        if (sender is NativeMenuItem { CommandParameter: string route } &&
            DataContext is MainWindowViewModel viewModel)
            _ = viewModel.NavigateToAsync(route);
    }

    private void CloseWindowMenuItem_OnClick(object? sender, EventArgs e) => Close();

    private void EditNativeMenu_OnNeedsUpdate(object? sender, EventArgs e)
    {
        if (sender is not NativeMenu menu) return;
        var textBox = FindFocusedTextBox();
        SetMenuItemEnabled(menu, "undo", textBox?.CanUndo == true);
        SetMenuItemEnabled(menu, "redo", textBox?.CanRedo == true);
        SetMenuItemEnabled(menu, "cut", textBox?.CanCut == true);
        SetMenuItemEnabled(menu, "copy", textBox?.CanCopy == true);
        SetMenuItemEnabled(menu, "paste", textBox?.CanPaste == true);
        SetMenuItemEnabled(menu, "select-all", textBox is not null);
    }

    private static void SetMenuItemEnabled(NativeMenu menu, string action, bool enabled)
    {
        var item = menu.Items.OfType<NativeMenuItem>()
            .FirstOrDefault(candidate => string.Equals(candidate.CommandParameter as string, action, StringComparison.Ordinal));
        if (item is not null) item.IsEnabled = enabled;
    }

    private TextBox? FindFocusedTextBox()
    {
        var focused = FocusManager?.GetFocusedElement();
        return focused as TextBox ??
               (focused as Visual)?.GetVisualAncestors().OfType<TextBox>().FirstOrDefault();
    }

    private void UndoMenuItem_OnClick(object? sender, EventArgs e) => FindFocusedTextBox()?.Undo();
    private void RedoMenuItem_OnClick(object? sender, EventArgs e) => FindFocusedTextBox()?.Redo();
    private void CutMenuItem_OnClick(object? sender, EventArgs e) => FindFocusedTextBox()?.Cut();
    private void CopyMenuItem_OnClick(object? sender, EventArgs e) => FindFocusedTextBox()?.Copy();
    private void PasteMenuItem_OnClick(object? sender, EventArgs e) => FindFocusedTextBox()?.Paste();
    private void SelectAllMenuItem_OnClick(object? sender, EventArgs e) => FindFocusedTextBox()?.SelectAll();

    private void ViewNativeMenu_OnNeedsUpdate(object? sender, EventArgs e)
    {
        if (sender is not NativeMenu menu) return;
        var route = (DataContext as MainWindowViewModel)?.CurrentRoute ?? "dashboard";
        foreach (var menuItem in menu.Items.OfType<NativeMenuItem>())
        {
            if (menuItem.CommandParameter is string itemRoute && itemRoute != "full-screen")
                menuItem.IsChecked = route.StartsWith(itemRoute, StringComparison.OrdinalIgnoreCase);
        }

        var fullScreenItem = menu.Items.OfType<NativeMenuItem>()
            .FirstOrDefault(item => string.Equals(item.CommandParameter as string, "full-screen", StringComparison.Ordinal));
        if (fullScreenItem is not null)
            fullScreenItem.Header = WindowState == WindowState.FullScreen ? "Exit Full Screen" : "Enter Full Screen";
    }

    private void FullScreenMenuItem_OnClick(object? sender, EventArgs e)
        => WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;

    private void MinimizeWindowMenuItem_OnClick(object? sender, EventArgs e)
        => WindowState = WindowState.Minimized;

    private void ZoomWindowMenuItem_OnClick(object? sender, EventArgs e) => ToggleMaximizeRestore();

    private void BringAllToFrontMenuItem_OnClick(object? sender, EventArgs e)
    {
        Show();
        Activate();
    }

    private static void RadioVaultHelpMenuItem_OnClick(object? sender, EventArgs e)
        => OpenExternalTarget("https://github.com/GHRobson/TheRadioVault#readme");

    private static void ReportProblemMenuItem_OnClick(object? sender, EventArgs e)
        => OpenExternalTarget("https://github.com/GHRobson/TheRadioVault/issues");

    private static void OpenDiagnosticsFolderMenuItem_OnClick(object? sender, EventArgs e)
    {
        var startInfo = new ProcessStartInfo("open") { UseShellExecute = false };
        startInfo.ArgumentList.Add(AvaloniaAppPaths.DataDirectory);
        Process.Start(startInfo);
    }

    private static void OpenExternalTarget(string target)
        => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });

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
