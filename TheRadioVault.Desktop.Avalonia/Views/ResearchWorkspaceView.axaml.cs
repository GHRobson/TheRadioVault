using Avalonia.Controls;
using Avalonia.Input;
using TheRadioVault.Presentation.ViewModels;

namespace TheRadioVault.Desktop.Avalonia.Views;

public partial class ResearchWorkspaceView : UserControl
{
    public ResearchWorkspaceView() => InitializeComponent();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not ResearchWorkspaceViewModel { IsDateReviewMode: true } viewModel)
            return;
        if (e.Source is TextBox or DatePicker) return;

        var command = e.Key switch
        {
            Key.A when e.KeyModifiers == KeyModifiers.None => viewModel.ApproveDateCommand,
            Key.K when e.KeyModifiers == KeyModifiers.None => viewModel.KeepExistingDateCommand,
            Key.I when e.KeyModifiers == KeyModifiers.None => viewModel.IgnoreDateCommand,
            Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Control) => viewModel.UndoDateDecisionCommand,
            _ => null
        };
        if (command?.CanExecute(null) != true) return;
        command.Execute(null);
        e.Handled = true;
    }

}
