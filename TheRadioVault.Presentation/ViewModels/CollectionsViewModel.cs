using System.Collections.ObjectModel;
using System.Windows.Input;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class CollectionsViewModel : ObservableObject
{
    private readonly ISavedCollectionService _collections;
    private readonly IQueueService _queue;
    private readonly PlaybackViewModel _playback;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private SavedCollectionSummary? _selectedCollection;
    private BroadcastRowViewModel? _selectedBroadcast;
    private SavedCollectionDetails? _details;
    private string _newName = string.Empty;
    private string _smartSearchText = string.Empty;
    private LibraryFilterOptionViewModel _selectedSmartFilter;
    private string _statusText = "Playlists and smart collections have not been loaded yet.";
    private bool _isBusy;
    private bool _isLoaded;

    public CollectionsViewModel(
        ISavedCollectionService collections,
        IQueueService queue,
        PlaybackViewModel playback)
    {
        _collections = collections ?? throw new ArgumentNullException(nameof(collections));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        SmartFilters =
        [
            new("Every broadcast", LibraryListeningFilter.All),
            new("Favourites", LibraryListeningFilter.Favourites),
            new("Continue listening", LibraryListeningFilter.ContinueListening),
            new("Unplayed", LibraryListeningFilter.Unplayed),
            new("Recently added", LibraryListeningFilter.RecentlyAdded),
            new("Completed", LibraryListeningFilter.Completed)
        ];
        _selectedSmartFilter = SmartFilters[0];
        RefreshCommand = new AsyncCommand(() => LoadAsync(force: true), () => !IsBusy, SetError);
        CreatePlaylistCommand = new AsyncCommand(CreatePlaylistAsync, CanCreate, SetError);
        CreateSmartCollectionCommand = new AsyncCommand(CreateSmartCollectionAsync, CanCreate, SetError);
        SaveQueueCommand = new AsyncCommand(SaveQueueAsync, CanCreate, SetError);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => !IsBusy && SelectedCollection is not null, SetError);
        RemoveBroadcastCommand = new AsyncCommand(RemoveBroadcastAsync,
            () => !IsBusy && Details?.Summary.Kind == SavedCollectionKind.Manual && SelectedBroadcast is not null,
            SetError);
        AddCurrentBroadcastCommand = new AsyncCommand(AddCurrentBroadcastAsync,
            () => !IsBusy && Details?.Summary.Kind == SavedCollectionKind.Manual,
            SetError);
    }

    public ObservableCollection<SavedCollectionSummary> Items { get; } = new();
    public ObservableCollection<BroadcastRowViewModel> Broadcasts { get; } = new();
    public IReadOnlyList<LibraryFilterOptionViewModel> SmartFilters { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CreatePlaylistCommand { get; }
    public ICommand CreateSmartCollectionCommand { get; }
    public ICommand SaveQueueCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand RemoveBroadcastCommand { get; }
    public ICommand AddCurrentBroadcastCommand { get; }
    public SavedCollectionDetails? Details { get => _details; private set { if (SetProperty(ref _details, value)) RaiseDetailProperties(); } }
    public SavedCollectionSummary? SelectedCollection
    {
        get => _selectedCollection;
        set
        {
            if (!SetProperty(ref _selectedCollection, value)) return;
            RaiseDetailProperties();
            _ = LoadSelectedAsync(value?.Id);
        }
    }
    public BroadcastRowViewModel? SelectedBroadcast
    {
        get => _selectedBroadcast;
        set
        {
            if (!SetProperty(ref _selectedBroadcast, value)) return;
            RaiseCommandState();
        }
    }
    public string NewName
    {
        get => _newName;
        set { if (SetProperty(ref _newName, value)) RaiseCommandState(); }
    }
    public string SmartSearchText { get => _smartSearchText; set => SetProperty(ref _smartSearchText, value); }
    public LibraryFilterOptionViewModel SelectedSmartFilter { get => _selectedSmartFilter; set => SetProperty(ref _selectedSmartFilter, value); }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandState(); } }
    public bool HasCollections => Items.Count > 0;
    public bool HasSelection => Details is not null;
    public bool HasBroadcasts => Broadcasts.Count > 0;
    public bool IsManual => Details?.Summary.Kind == SavedCollectionKind.Manual;
    public string SelectedKindText => Details?.Summary.KindText ?? string.Empty;
    public string SelectedCountText => Details?.Summary.CountText ?? string.Empty;

    public async Task LoadAsync(bool force = false)
    {
        await _loadGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_isLoaded && !force) return;
            IsBusy = true;
            StatusText = "Loading playlists and smart collections…";
            var selectedId = SelectedCollection?.Id;
            var records = await _collections.GetAllAsync().ConfigureAwait(true);
            Items.Clear();
            foreach (var item in records) Items.Add(item);
            RaisePropertyChanged(nameof(HasCollections));
            var selection = Items.FirstOrDefault(value => value.Id == selectedId) ?? Items.FirstOrDefault();
            if (ReferenceEquals(selection, SelectedCollection))
                await LoadSelectedAsync(selection?.Id).ConfigureAwait(true);
            else
                SelectedCollection = selection;
            StatusText = Items.Count == 0
                ? "No saved collections yet. Create a playlist or a live smart collection."
                : $"{Items.Count:N0} saved collection{(Items.Count == 1 ? string.Empty : "s")}.";
            _isLoaded = true;
        }
        catch (Exception exception) { SetError(exception); }
        finally
        {
            IsBusy = false;
            _loadGate.Release();
        }
    }

    private async Task LoadSelectedAsync(long? collectionId)
    {
        if (!collectionId.HasValue)
        {
            Details = null;
            Broadcasts.Clear();
            RaisePropertyChanged(nameof(HasBroadcasts));
            return;
        }
        try
        {
            var details = await _collections.GetAsync(collectionId.Value).ConfigureAwait(true);
            if (SelectedCollection?.Id != collectionId.Value) return;
            ApplyDetails(details);
        }
        catch (Exception exception) { SetError(exception); }
    }

    private async Task CreatePlaylistAsync()
    {
        await CreateAsync(SavedCollectionKind.Manual, rule: null, episodeIds: null).ConfigureAwait(true);
    }

    private async Task CreateSmartCollectionAsync()
    {
        var rule = new SavedCollectionRule(
            SearchText: SmartSearchText,
            Filter: SelectedSmartFilter.Filter,
            Limit: 500);
        await CreateAsync(SavedCollectionKind.Smart, rule, episodeIds: null).ConfigureAwait(true);
    }

    private async Task SaveQueueAsync()
    {
        var queue = await _queue.GetAsync().ConfigureAwait(true);
        await CreateAsync(
            SavedCollectionKind.Manual,
            rule: null,
            queue.Select(value => value.BroadcastId).ToArray()).ConfigureAwait(true);
    }

    private async Task CreateAsync(
        SavedCollectionKind kind,
        SavedCollectionRule? rule,
        IReadOnlyList<long>? episodeIds)
    {
        if (!CanCreate()) return;
        IsBusy = true;
        try
        {
            var created = await _collections.CreateAsync(NewName, kind, rule, episodeIds).ConfigureAwait(true);
            NewName = string.Empty;
            SmartSearchText = string.Empty;
            _isLoaded = false;
            await LoadAsync(force: true).ConfigureAwait(true);
            SelectedCollection = Items.FirstOrDefault(value => value.Id == created.Summary.Id);
            StatusText = kind == SavedCollectionKind.Smart
                ? "Smart collection created. It will update from the Library automatically."
                : episodeIds is not null ? "Up Next saved as a reusable playlist." : "Playlist created.";
        }
        finally { IsBusy = false; }
    }

    private async Task DeleteAsync()
    {
        var selected = SelectedCollection;
        if (selected is null) return;
        IsBusy = true;
        try
        {
            await _collections.DeleteAsync(selected.Id, selected.Revision).ConfigureAwait(true);
            SelectedCollection = null;
            _isLoaded = false;
            await LoadAsync(force: true).ConfigureAwait(true);
            StatusText = "Saved collection deleted. Its broadcasts remain in the Library.";
        }
        catch (SavedCollectionConflictException)
        {
            _isLoaded = false;
            await LoadAsync(force: true).ConfigureAwait(true);
            StatusText = "This collection changed on another device. Radio Vault reloaded the current version.";
        }
        finally { IsBusy = false; }
    }

    private async Task RemoveBroadcastAsync()
    {
        var details = Details;
        var broadcast = SelectedBroadcast;
        if (details is null || broadcast is null || details.Summary.Kind != SavedCollectionKind.Manual) return;
        IsBusy = true;
        try
        {
            ApplyDetails(await _collections.RemoveAsync(
                details.Summary.Id,
                broadcast.Source.RepresentativeEpisodeId,
                details.Summary.Revision).ConfigureAwait(true));
            ReplaceSummary(Details!.Summary);
            StatusText = "Broadcast removed from the playlist.";
        }
        catch (SavedCollectionConflictException)
        {
            await LoadSelectedAsync(details.Summary.Id).ConfigureAwait(true);
            StatusText = "This playlist changed on another device. Radio Vault reloaded it before making another edit.";
        }
        finally { IsBusy = false; }
    }

    private async Task AddCurrentBroadcastAsync()
    {
        var details = Details;
        if (details is null || details.Summary.Kind != SavedCollectionKind.Manual) return;
        if (!_playback.HasCurrentBroadcast)
        {
            StatusText = "Play or open a broadcast first, then add it to this playlist.";
            return;
        }
        IsBusy = true;
        try
        {
            var previousRevision = details.Summary.Revision;
            var updated = await _collections.AddAsync(
                details.Summary.Id,
                _playback.CurrentBroadcastId,
                previousRevision).ConfigureAwait(true);
            ApplyDetails(updated);
            ReplaceSummary(updated.Summary);
            StatusText = updated.Summary.Revision == previousRevision
                ? "That broadcast is already in this playlist."
                : "Current broadcast added to the playlist.";
        }
        catch (SavedCollectionConflictException)
        {
            await LoadSelectedAsync(details.Summary.Id).ConfigureAwait(true);
            StatusText = "This playlist changed on another device. Radio Vault reloaded it before making another edit.";
        }
        finally { IsBusy = false; }
    }

    private void ApplyDetails(SavedCollectionDetails? details)
    {
        Details = details;
        Broadcasts.Clear();
        if (details is not null)
            foreach (var broadcast in details.Broadcasts)
                Broadcasts.Add(new BroadcastRowViewModel(broadcast, _playback.LoadAndPlayAsync));
        SelectedBroadcast = Broadcasts.FirstOrDefault();
        RaisePropertyChanged(nameof(HasBroadcasts));
    }

    private void ReplaceSummary(SavedCollectionSummary summary)
    {
        var index = Items.ToList().FindIndex(value => value.Id == summary.Id);
        if (index >= 0) Items[index] = summary;
        SelectedCollection = summary;
    }

    private bool CanCreate() => !IsBusy && !string.IsNullOrWhiteSpace(NewName);

    private void RaiseDetailProperties()
    {
        RaisePropertyChanged(nameof(HasSelection));
        RaisePropertyChanged(nameof(IsManual));
        RaisePropertyChanged(nameof(SelectedKindText));
        RaisePropertyChanged(nameof(SelectedCountText));
        RaiseCommandState();
    }

    private void RaiseCommandState()
    {
        foreach (var command in new[] { RefreshCommand, CreatePlaylistCommand, CreateSmartCollectionCommand, SaveQueueCommand, DeleteCommand, RemoveBroadcastCommand, AddCurrentBroadcastCommand })
            ((AsyncCommand)command).RaiseCanExecuteChanged();
    }

    private void SetError(Exception exception)
        => StatusText = "Saved collections could not be updated: " + exception.Message;
}
