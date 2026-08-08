using System.Windows.Input;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class QueueItemViewModel
{
    public QueueItemViewModel(
        QueueRecord source,
        int itemCount,
        Func<QueueItemViewModel, Task> play,
        Func<QueueItemViewModel, Task> remove,
        Func<QueueItemViewModel, int, Task> move)
    {
        Source = source ?? throw new ArgumentNullException(nameof(source));
        Title = string.IsNullOrWhiteSpace(source.BroadcastTitle)
            ? source.AirDate?.ToString("dddd, d MMMM yyyy") ?? source.OriginalFilename
            : source.BroadcastTitle.Trim();
        DateText = source.AirDate?.ToString("ddd, d MMM yyyy") ?? "Date unknown";
        PositionText = $"{source.Position + 1}";
        CanMoveUp = source.Position > 0;
        CanMoveDown = source.Position < itemCount - 1;
        PlayCommand = new AsyncCommand(() => play(this));
        RemoveCommand = new AsyncCommand(() => remove(this));
        MoveUpCommand = new AsyncCommand(() => move(this, -1), () => CanMoveUp);
        MoveDownCommand = new AsyncCommand(() => move(this, 1), () => CanMoveDown);
    }

    public QueueRecord Source { get; }
    public string Title { get; }
    public string DateText { get; }
    public string PositionText { get; }
    public string CollectionText => Source.CollectionName;
    public string AddedText => Source.AddedAt == default ? "Queued" : $"Added {Source.AddedAt.LocalDateTime:g}";
    public bool CanMoveUp { get; }
    public bool CanMoveDown { get; }
    public ICommand PlayCommand { get; }
    public ICommand RemoveCommand { get; }
    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
}
