using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class MomentItemViewModel
{
    public MomentItemViewModel(MomentRecord source)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        BroadcastText = string.IsNullOrWhiteSpace(source.BroadcastTitle)
            ? source.AirDate?.ToString("dddd, d MMMM yyyy") ?? $"Broadcast {source.BroadcastId}"
            : source.BroadcastTitle.Trim();
    }

    public MomentRecord Source { get; }
    public string Title => string.IsNullOrWhiteSpace(Source.Title) ? "Untitled moment" : Source.Title;
    public string Notes => Source.Notes;
    public string BroadcastText { get; }
    public string CollectionText => Source.CollectionName;
    public string PositionText => FormatTime(Source.PositionMs);
    public string DateText => Source.AirDate?.ToString("ddd, d MMM yyyy") ?? "Date unknown";
    public string CreatedText => Source.CreatedAt == default ? string.Empty : Source.CreatedAt.LocalDateTime.ToString("g");
    public bool HasNotes => !string.IsNullOrWhiteSpace(Source.Notes);

    private static string FormatTime(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }
}
