using System.Windows.Input;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class SearchFacetChipViewModel : ObservableObject
{
    private bool _isSelected;

    public SearchFacetChipViewModel(string title, LibrarySearchScope scope, Action<SearchFacetChipViewModel> select)
    {
        Title = title;
        Scope = scope;
        SelectCommand = new DelegateCommand(() => select(this));
    }

    public string Title { get; }
    public LibrarySearchScope Scope { get; }
    public ICommand SelectCommand { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed class SearchStatusChipViewModel : ObservableObject
{
    private bool _isSelected;

    public SearchStatusChipViewModel(string title, LibraryListeningFilter filter, Action<SearchStatusChipViewModel> select)
    {
        Title = title;
        Filter = filter;
        SelectCommand = new DelegateCommand(() => select(this));
    }

    public string Title { get; }
    public LibraryListeningFilter Filter { get; }
    public ICommand SelectCommand { get; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed record SearchShowFacetViewModel(int? CollectionId, string Title);
public sealed record SearchYearFacetViewModel(int? Year, string Title);

public sealed class SearchSuggestionViewModel
{
    public SearchSuggestionViewModel(LibrarySearchSuggestion source, Action<string> select)
    {
        Source = source;
        SelectCommand = new DelegateCommand(() => select(source.Value));
    }

    public LibrarySearchSuggestion Source { get; }
    public string Value => Source.Value;
    public string Kind => Source.Kind;
    public string CountText => Source.MatchCount == 1 ? "1 match" : $"{Source.MatchCount:N0} matches";
    public ICommand SelectCommand { get; }
}
