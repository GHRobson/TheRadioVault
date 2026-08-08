using Avalonia.Controls;
using Avalonia.Input;
using TheRadioVault.Presentation.ViewModels;

namespace TheRadioVault.Desktop.Avalonia.Views;

public partial class NowPlayingView : UserControl
{
    public NowPlayingView() => InitializeComponent();

    private void PlaybackSeekSlider_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Slider slider && DataContext is NowPlayingViewModel viewModel)
            viewModel.Playback.SeekTo((long)slider.Value);
    }
}
