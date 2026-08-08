using Avalonia;
using Avalonia.iOS;
using Foundation;
using TheRadioVault.Client.Mobile;

namespace TheRadioVault.Client.iOS;

[Register("AppDelegate")]
#pragma warning disable CA1711
public partial class AppDelegate : AvaloniaAppDelegate<App>
#pragma warning restore CA1711
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder).LogToTrace();
}
