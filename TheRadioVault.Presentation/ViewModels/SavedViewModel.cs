using System.Windows.Input;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

public enum SavedSection
{
    Favourites,
    Moments,
    Collections
}

public sealed class SavedViewModel : ObservableObject
{
    private SavedSection _selectedSection = SavedSection.Favourites;
    private bool _isBusy;

    public SavedViewModel(LibraryViewModel library, MomentsViewModel moments, CollectionsViewModel collections)
    {
        Library = library ?? throw new ArgumentNullException(nameof(library));
        Moments = moments ?? throw new ArgumentNullException(nameof(moments));
        Collections = collections ?? throw new ArgumentNullException(nameof(collections));
        ShowFavouritesCommand = new AsyncCommand(
            () => SelectSectionAsync(SavedSection.Favourites, force: true),
            () => !IsBusy);
        ShowMomentsCommand = new AsyncCommand(
            () => SelectSectionAsync(SavedSection.Moments, force: true),
            () => !IsBusy);
        ShowCollectionsCommand = new AsyncCommand(
            () => SelectSectionAsync(SavedSection.Collections, force: true),
            () => !IsBusy);
    }

    public LibraryViewModel Library { get; }
    public MomentsViewModel Moments { get; }
    public CollectionsViewModel Collections { get; }
    public ICommand ShowFavouritesCommand { get; }
    public ICommand ShowMomentsCommand { get; }
    public ICommand ShowCollectionsCommand { get; }
    public SavedSection SelectedSection
    {
        get => _selectedSection;
        private set
        {
            if (!SetProperty(ref _selectedSection, value)) return;
            RaisePropertyChanged(nameof(IsFavouritesSelected));
            RaisePropertyChanged(nameof(IsMomentsSelected));
            RaisePropertyChanged(nameof(IsCollectionsSelected));
        }
    }
    public bool IsFavouritesSelected => SelectedSection == SavedSection.Favourites;
    public bool IsMomentsSelected => SelectedSection == SavedSection.Moments;
    public bool IsCollectionsSelected => SelectedSection == SavedSection.Collections;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            ((AsyncCommand)ShowFavouritesCommand).RaiseCanExecuteChanged();
            ((AsyncCommand)ShowMomentsCommand).RaiseCanExecuteChanged();
            ((AsyncCommand)ShowCollectionsCommand).RaiseCanExecuteChanged();
        }
    }

    public Task LoadAsync(bool force = false)
        => SelectSectionAsync(SelectedSection, force);

    public async Task SelectSectionAsync(SavedSection section, bool force = false)
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            SelectedSection = section;
            if (section == SavedSection.Moments)
            {
                await Moments.LoadAsync(force).ConfigureAwait(true);
                return;
            }
            if (section == SavedSection.Collections)
            {
                await Collections.LoadAsync(force).ConfigureAwait(true);
                return;
            }

            if (Library.SelectedCollectionId.HasValue)
                await Library.SelectCollectionAsync(null, null, force: false).ConfigureAwait(true);
            if (Library.SelectedFilter.Filter != LibraryListeningFilter.Favourites)
                await Library.SetListeningFilterAsync(LibraryListeningFilter.Favourites).ConfigureAwait(true);
            else
                await Library.LoadAsync(force).ConfigureAwait(true);
        }
        finally { IsBusy = false; }
    }
}
