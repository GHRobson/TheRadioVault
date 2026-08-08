using System.Collections.ObjectModel;
using System.Windows.Input;
using TheRadioVault.Presentation.Infrastructure;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class ShellNavigationItemViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isExpanded;

    public ShellNavigationItemViewModel(
        string route,
        string title,
        string description,
        string iconGlyph,
        Func<string, Task> navigate,
        string iconTone = "accent",
        bool isExpandable = false,
        bool isExpanded = false)
    {
        Route = route;
        Title = title;
        Description = description;
        IconGlyph = iconGlyph;
        IconTone = string.IsNullOrWhiteSpace(iconTone) ? "accent" : iconTone.Trim().ToLowerInvariant();
        IsExpandable = isExpandable;
        _isExpanded = isExpanded;
        SelectCommand = new AsyncCommand(async () =>
        {
            if (IsExpandable)
                IsExpanded = !IsExpanded;
            await navigate(Route).ConfigureAwait(true);
        });
    }

    public string Route { get; }
    public string Title { get; }
    public string Description { get; }
    public string IconGlyph { get; }
    public string IconTone { get; }
    public bool IsSearchTone => IconTone == "search";
    public bool IsProgressTone => IconTone == "progress";
    public bool IsFavouriteTone => IconTone == "favourite";
    public bool IsMomentTone => IconTone == "moment";
    public bool IsResearchTone => IconTone == "research";
    public bool IsWikiTone => IconTone == "wiki";
    public bool IsTranscriptTone => IconTone == "transcript";
    public bool IsSettingsTone => IconTone == "settings";
    public bool IsDashboardIcon => Route == "dashboard";
    public bool IsSearchIcon => Route == "search";
    public bool IsLibraryIcon => Route.StartsWith("library", StringComparison.OrdinalIgnoreCase);
    public bool IsFavouriteIcon => Route == "favourites";
    public bool IsMomentIcon => Route == "moments";
    public bool IsTranscriptsIcon => Route == "transcripts";
    public bool IsNowPlayingIcon => Route == "now-playing";
    public bool IsResearchIcon => Route == "research";
    public bool IsWikiIcon => Route == "wiki";
    public bool IsDownloadsIcon => Route == "downloads";
    public bool IsSettingsIcon => Route == "tools";
    public bool IsExpandable { get; }
    public ObservableCollection<ShellNavigationItemViewModel> Children { get; } = new();
    public bool HasChildren => Children.Count > 0;
    public string ExpansionGlyph => IsExpanded ? "⌄" : "›";
    public bool IsSelected { get => _isSelected; internal set => SetProperty(ref _isSelected, value); }
    public bool IsExpanded
    {
        get => _isExpanded;
        internal set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            RaisePropertyChanged(nameof(ExpansionGlyph));
        }
    }
    public ICommand SelectCommand { get; }

    public void ReplaceChildren(IEnumerable<ShellNavigationItemViewModel> children)
    {
        Children.Clear();
        foreach (var child in children) Children.Add(child);
        RaisePropertyChanged(nameof(HasChildren));
    }
}
