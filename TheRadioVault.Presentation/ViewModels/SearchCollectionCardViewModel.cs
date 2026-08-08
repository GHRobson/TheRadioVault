using System.Windows.Input;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class SearchCollectionCardViewModel
{
    public SearchCollectionCardViewModel(
        string title,
        string description,
        string glyph,
        Func<Task> open,
        int? count = null)
    {
        Title = title;
        Description = description;
        Glyph = glyph;
        Count = count;
        OpenCommand = new AsyncCommand(open);
    }

    public string Title { get; }
    public string Description { get; }
    public string Glyph { get; }
    public int? Count { get; }
    public string CountText => Count.HasValue ? $"{Count.Value:N0} broadcasts" : string.Empty;
    public bool HasCount => Count.HasValue;
    public ICommand OpenCommand { get; }
}
