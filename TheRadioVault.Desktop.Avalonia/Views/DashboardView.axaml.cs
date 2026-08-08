using Avalonia.Controls;
using Avalonia.Threading;
using TheRadioVault.Presentation.ViewModels;

namespace TheRadioVault.Desktop.Avalonia.Views;

public partial class DashboardView : UserControl
{
    private readonly DispatcherTimer _onThisDayTimer;

    public DashboardView()
    {
        InitializeComponent();
        _onThisDayTimer = new DispatcherTimer(TimeSpan.FromSeconds(8), DispatcherPriority.Background, (_, _) =>
        {
            if (DataContext is DashboardViewModel viewModel && viewModel.HasOnThisDay)
                viewModel.MoveOnThisDay(1);
        });
    }

    private void DashboardView_OnAttachedToVisualTree(object? sender, global::Avalonia.VisualTreeAttachmentEventArgs e)
        => _onThisDayTimer.Start();

    private void DashboardView_OnDetachedFromVisualTree(object? sender, global::Avalonia.VisualTreeAttachmentEventArgs e)
        => _onThisDayTimer.Stop();
}
