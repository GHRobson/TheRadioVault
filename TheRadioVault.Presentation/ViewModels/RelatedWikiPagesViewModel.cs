using System.Collections.ObjectModel;
using System.Windows.Input;
using TheRadioVault.Core.Domain;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class RelatedWikiPagesViewModel : ObservableObject
{
    private readonly IWikiService? _wiki;
    private Func<WikiPageSummary, Task>? _open;
    private Func<string, Task>? _openEntity;
    private Func<ArchiveEntityLink, Task>? _openEntityLink;
    private bool _isLoading;
    private string _context = string.Empty;

    public RelatedWikiPagesViewModel(IWikiService? wiki)
    {
        _wiki = wiki;
        OpenCommand = new DelegateCommand(parameter =>
        {
            if (parameter is WikiPageSummary page && _open is not null) _ = _open(page);
        });
        OpenEntityCommand = new DelegateCommand(parameter =>
        {
            if (parameter is ArchiveEntityLink link && _openEntityLink is not null)
            {
                _ = _openEntityLink(link);
                return;
            }
            var entity = parameter?.ToString()?.Trim();
            if (!string.IsNullOrWhiteSpace(entity) && _openEntity is not null) _ = _openEntity(entity);
        });
    }

    public ObservableCollection<WikiPageSummary> Pages { get; } = new();
    public ICommand OpenCommand { get; }
    public ICommand OpenEntityCommand { get; }
    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }
    public bool HasPages => Pages.Count > 0;
    public string Context { get => _context; private set => SetProperty(ref _context, value); }

    public void SetOpenHandler(Func<WikiPageSummary, Task> handler) => _open = handler;
    public void SetOpenEntityHandler(Func<string, Task> handler) => _openEntity = handler;
    public void SetOpenEntityLinkHandler(Func<ArchiveEntityLink, Task> handler) => _openEntityLink = handler;

    public async Task LoadAsync(params string?[] terms)
    {
        if (_wiki is null) return;
        var normalized = terms.Where(x => !string.IsNullOrWhiteSpace(x))
            .SelectMany(x => x!.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(x => x.Length >= 2).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
        Context = string.Join(" · ", normalized.Take(4));
        IsLoading = true;
        try
        {
            var found = new Dictionary<Guid, (WikiPageSummary Page, int Score)>();
            foreach (var term in normalized)
            {
                var values = await _wiki.BrowseAsync(new WikiBrowseQuery(term, Limit: 30)).ConfigureAwait(true);
                foreach (var page in values)
                {
                    var score = string.Equals(page.Title, term, StringComparison.OrdinalIgnoreCase) ? 100
                        : page.Title.Contains(term, StringComparison.OrdinalIgnoreCase) ? 60
                        : page.Summary.Contains(term, StringComparison.OrdinalIgnoreCase) ? 30 : 10;
                    if (!found.TryGetValue(page.PageId, out var existing) || score > existing.Score) found[page.PageId] = (page, score);
                }
            }
            Pages.Clear();
            foreach (var match in found.Values.OrderByDescending(x => x.Score)
                         .ThenByDescending(x => x.Page.Status == "Published")
                         .ThenBy(x => x.Page.Title).Take(6)) Pages.Add(match.Page);
            RaisePropertyChanged(nameof(HasPages));
        }
        finally { IsLoading = false; }
    }

    public void Clear()
    {
        Pages.Clear();
        Context = string.Empty;
        RaisePropertyChanged(nameof(HasPages));
    }
}
