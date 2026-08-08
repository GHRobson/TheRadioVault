using System.Collections.ObjectModel;
using System.Windows.Input;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class QueueViewModel : ObservableObject, IDisposable
{
    private readonly IQueueService _queue;
    private readonly PlaybackViewModel _playback;
    private bool _isBusy;
    private bool _isLoaded;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private string _statusText = "The queue has not been loaded yet.";

    public QueueViewModel(IQueueService queue, PlaybackViewModel playback)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        RefreshCommand = new AsyncCommand(() => LoadAsync(force: true), () => !IsBusy, SetError);
        ClearCommand = new AsyncCommand(ClearAsync, () => !IsBusy && HasItems, SetError);
        PlayFirstCommand = new AsyncCommand(PlayFirstAsync, () => !IsBusy && HasItems, SetError);
        _playback.QueueChanged += PlaybackOnQueueChanged;
    }

    public ObservableCollection<QueueItemViewModel> Items { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand PlayFirstCommand { get; }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandState(); } }
    public bool HasItems => Items.Count > 0;
    public int Count => Items.Count;
    public string CountText => Count == 1 ? "1 queued broadcast" : $"{Count:N0} queued broadcasts";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public async Task LoadAsync(bool force = false)
    {
        if (_isLoaded && !force && ConnectedViewRefreshPolicy.IsFresh(_loadedAt)) return;
        IsBusy = true;
        StatusText = "Loading the persistent queue…";
        try
        {
            var records = await _queue.GetAsync().ConfigureAwait(true);
            Items.Clear();
            foreach (var record in records)
                Items.Add(new QueueItemViewModel(record, records.Count, PlayAsync, RemoveAsync, MoveAsync));
            RaiseCollectionState();
            StatusText = HasItems
                ? "Queue order is stored in the library and restored after restart."
                : "The queue is empty. Add broadcasts from Library, Dashboard, or Now Playing.";
            _isLoaded = true;
            _loadedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception exception) { SetError(exception); }
        finally { IsBusy = false; }
    }

    public async Task AddAsync(long broadcastId, bool playNext)
    {
        if (broadcastId <= 0) return;
        await _queue.AddAsync(broadcastId, playNext).ConfigureAwait(true);
        _isLoaded = false;
        await LoadAsync(force: true).ConfigureAwait(true);
        StatusText = playNext ? "Broadcast added to the front of the queue." : "Broadcast added to the queue.";
    }

    private async Task PlayFirstAsync()
    {
        var first = Items.FirstOrDefault();
        if (first is not null) await PlayAsync(first).ConfigureAwait(true);
    }

    private async Task PlayAsync(QueueItemViewModel item)
    {
        IsBusy = true;
        try
        {
            await _playback.LoadAndPlayAsync(item.Source.BroadcastId).ConfigureAwait(true);
            if (!_playback.IsLoaded || _playback.CurrentBroadcastId != item.Source.BroadcastId)
                throw new InvalidOperationException("The queued broadcast could not be opened, so it remains in the queue.");
            await _queue.RemoveAsync(item.Source.Id).ConfigureAwait(true);
            _isLoaded = false;
            await LoadAsync(force: true).ConfigureAwait(true);
            StatusText = $"Playing {item.Title}.";
        }
        finally { IsBusy = false; }
    }

    private async Task RemoveAsync(QueueItemViewModel item)
    {
        await _queue.RemoveAsync(item.Source.Id).ConfigureAwait(true);
        _isLoaded = false;
        await LoadAsync(force: true).ConfigureAwait(true);
    }

    private async Task MoveAsync(QueueItemViewModel item, int direction)
    {
        await _queue.MoveAsync(item.Source.Id, direction).ConfigureAwait(true);
        _isLoaded = false;
        await LoadAsync(force: true).ConfigureAwait(true);
    }

    private async Task ClearAsync()
    {
        IsBusy = true;
        try
        {
            await _queue.ClearAsync().ConfigureAwait(true);
            Items.Clear();
            _isLoaded = true;
            RaiseCollectionState();
            StatusText = "Queue cleared.";
        }
        finally { IsBusy = false; }
    }

    private void RaiseCollectionState()
    {
        RaisePropertyChanged(nameof(HasItems));
        RaisePropertyChanged(nameof(Count));
        RaisePropertyChanged(nameof(CountText));
        RaiseCommandState();
    }

    private void RaiseCommandState()
    {
        ((AsyncCommand)RefreshCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ClearCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)PlayFirstCommand).RaiseCanExecuteChanged();
    }

    private void PlaybackOnQueueChanged(object? sender, EventArgs e)
    {
        _isLoaded = false;
        _ = LoadAsync(force: true);
    }

    private void SetError(Exception exception) => StatusText = $"Queue action failed: {exception.Message}";

    public void Dispose() => _playback.QueueChanged -= PlaybackOnQueueChanged;
}
