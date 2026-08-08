using System.Windows.Input;
using TheRadioVault.Presentation.Infrastructure;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class DashboardCarouselDotViewModel : ObservableObject
{
    private bool _isActive;

    public DashboardCarouselDotViewModel(int index, Action<int> select)
    {
        Index = index;
        SelectCommand = new DelegateCommand(() => select(Index));
    }

    public int Index { get; }
    public ICommand SelectCommand { get; }
    public bool IsActive { get => _isActive; private set => SetProperty(ref _isActive, value); }
    public void SetActive(bool active) => IsActive = active;
}
