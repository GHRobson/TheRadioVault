using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using TheRadioVault.Client.Mobile.Platform;
using TheRadioVault.Client.Mobile.ViewModels;
using TheRadioVault.Client.Mobile.Views;

namespace TheRadioVault.Client.Mobile;

public partial class App : Application
{
    private MobileMainViewModel? _main;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            var platform = MobilePlatformServices.Current;
            _main = new MobileMainViewModel(
                new MobileServerClient(platform.ConnectionStore),
                platform.PlaybackEngine);
            singleView.MainView = new MainView { DataContext = _main };
            _ = _main.InitializeAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
