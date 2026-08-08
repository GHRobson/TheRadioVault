using System.Windows.Input;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class LibraryArchivePeriodViewModel
{
    public LibraryArchivePeriodViewModel(LibraryArchivePeriodSummary source, Func<LibraryArchivePeriodViewModel, Task> open)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        OpenCommand = new AsyncCommand(() => open(this));
    }

    public LibraryArchivePeriodSummary Source { get; }
    public int Value => Source.Value;
    public string Title => Source.Title;
    public string ShowsText => Source.ShowsText;
    public string BroadcastCountText => Source.BroadcastCount == 1 ? "1 broadcast" : $"{Source.BroadcastCount:N0} broadcasts";
    public string ProgressText => Source.ProgressText;
    public int ProgressPercent => Source.ProgressPercent;
    public string ProgressPercentText => $"{ProgressPercent}% listened";
    public string FavouriteCountText => Source.FavouriteCount == 0
        ? "No favourites"
        : Source.FavouriteCount == 1 ? "1 favourite" : $"{Source.FavouriteCount:N0} favourites";
    public string? ArtworkPath => Source.ArtworkPath;
    public bool HasArtwork => !string.IsNullOrWhiteSpace(ArtworkPath) && File.Exists(ArtworkPath);
    public ICommand OpenCommand { get; }
}
