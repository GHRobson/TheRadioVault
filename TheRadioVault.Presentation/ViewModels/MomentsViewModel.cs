using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class MomentsViewModel : ObservableObject, IDisposable
{
    private readonly IMomentsService _moments;
    private readonly PlaybackViewModel _playback;
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private bool _isBusy;
    private bool _isLoaded;
    private DateTimeOffset _loadedAt = DateTimeOffset.MinValue;
    private string _searchText = string.Empty;
    private string _statusText = "Moments have not been loaded yet.";
    private MomentItemViewModel? _selectedMoment;
    private string _editorTitle = string.Empty;
    private string _editorNotes = string.Empty;
    private bool _disposed;
    private int _queryVersion;
    private CancellationTokenSource? _queryDebounce;
    private bool _suppressAutomaticRefresh;

    public MomentsViewModel(IMomentsService moments, PlaybackViewModel playback)
    {
        _moments = moments ?? throw new ArgumentNullException(nameof(moments));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        SearchCommand = new AsyncCommand(() => LoadAsync(force: true), () => !IsBusy, SetError);
        ClearSearchCommand = new AsyncCommand(async () =>
        {
            _suppressAutomaticRefresh = true;
            try { SearchText = string.Empty; }
            finally { _suppressAutomaticRefresh = false; }
            await LoadAsync(force: true).ConfigureAwait(true);
        }, () => !IsBusy, SetError);
        CreateCurrentCommand = new AsyncCommand(CreateCurrentAsync, () => !IsBusy && CanCreateCurrent, SetError);
        SaveCommand = new AsyncCommand(SaveAsync, () => !IsBusy && HasSelection, SetError);
        DeleteCommand = new AsyncCommand(DeleteAsync, () => !IsBusy && HasSelection, SetError);
        JumpCommand = new AsyncCommand(JumpAsync, () => !IsBusy && HasSelection, SetError);
        _playback.PropertyChanged += PlaybackOnPropertyChanged;
    }

    public ObservableCollection<MomentItemViewModel> Items { get; } = new();
    public ICommand SearchCommand { get; }
    public ICommand ClearSearchCommand { get; }
    public ICommand CreateCurrentCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand JumpCommand { get; }
    public bool IsBusy { get => _isBusy; private set { if (SetProperty(ref _isBusy, value)) RaiseCommandState(); } }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            _queryVersion++;
            RaisePropertyChanged(nameof(HasActiveSearch));
            if (!_suppressAutomaticRefresh && _isLoaded) ScheduleSearchRefresh();
        }
    }
    public bool HasActiveSearch => !string.IsNullOrWhiteSpace(SearchText);
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public bool HasItems => Items.Count > 0;
    public bool HasSelection => SelectedMoment is not null;
    public bool CanCreateCurrent => _playback.HasCurrentBroadcast;
    public string CreateCurrentText => CanCreateCurrent ? $"Save moment at {_playback.PositionText}" : "Play a broadcast to save a moment";
    public MomentItemViewModel? SelectedMoment
    {
        get => _selectedMoment;
        set
        {
            if (!SetProperty(ref _selectedMoment, value)) return;
            EditorTitle = value?.Source.Title ?? string.Empty;
            EditorNotes = value?.Source.Notes ?? string.Empty;
            RaisePropertyChanged(nameof(HasSelection));
            RaiseCommandState();
        }
    }
    public string EditorTitle { get => _editorTitle; set => SetProperty(ref _editorTitle, value); }
    public string EditorNotes { get => _editorNotes; set => SetProperty(ref _editorNotes, value); }

    public async Task LoadAsync(bool force = false)
    {
        var requestVersion = _queryVersion;
        await _loadGate.WaitAsync().ConfigureAwait(true);
        try
        {
            if (_isLoaded && !force && ConnectedViewRefreshPolicy.IsFresh(_loadedAt)) return;
            IsBusy = true;
            StatusText = "Loading saved Moments…";
            var selectedId = SelectedMoment?.Source.Id;
            var records = await _moments.SearchAsync(SearchText, 1000).ConfigureAwait(true);
            if (requestVersion != _queryVersion) return;

            Items.Clear();
            foreach (var record in records) Items.Add(new MomentItemViewModel(record));
            SelectedMoment = Items.FirstOrDefault(x => x.Source.Id == selectedId) ?? Items.FirstOrDefault();
            RaisePropertyChanged(nameof(HasItems));
            StatusText = HasItems
                ? $"{Items.Count:N0} saved Moment{(Items.Count == 1 ? string.Empty : "s")}."
                : "No Moments match this search.";
            _isLoaded = true;
            _loadedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception exception) { SetError(exception); }
        finally
        {
            IsBusy = false;
            _loadGate.Release();
        }
    }

    private void ScheduleSearchRefresh()
    {
        _queryDebounce?.Cancel();
        _queryDebounce?.Dispose();
        _queryDebounce = new CancellationTokenSource();
        var token = _queryDebounce.Token;
        _ = RefreshAfterPauseAsync(token);
    }

    private async Task RefreshAfterPauseAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(220, cancellationToken).ConfigureAwait(true);
            await LoadAsync(force: true).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { }
    }

    private async Task CreateCurrentAsync()
    {
        if (!_playback.HasCurrentBroadcast) return;
        IsBusy = true;
        try
        {
            var title = $"Moment at {_playback.PositionText}";
            var id = await _moments.AddAsync(
                _playback.CurrentBroadcastId,
                _playback.PositionMs,
                title,
                string.Empty).ConfigureAwait(true);
            _isLoaded = false;
            await LoadAsync(force: true).ConfigureAwait(true);
            SelectedMoment = Items.FirstOrDefault(x => x.Source.Id == id) ?? SelectedMoment;
            StatusText = "Moment saved. Add a title or notes in the editor.";
        }
        finally { IsBusy = false; }
    }

    private async Task SaveAsync()
    {
        var selected = SelectedMoment;
        if (selected is null) return;
        await _moments.UpdateAsync(selected.Source.Id, EditorTitle, EditorNotes).ConfigureAwait(true);
        _isLoaded = false;
        await LoadAsync(force: true).ConfigureAwait(true);
        SelectedMoment = Items.FirstOrDefault(x => x.Source.Id == selected.Source.Id) ?? SelectedMoment;
        StatusText = "Moment updated.";
    }

    private async Task DeleteAsync()
    {
        var selected = SelectedMoment;
        if (selected is null) return;
        await _moments.DeleteAsync(selected.Source.Id).ConfigureAwait(true);
        _isLoaded = false;
        await LoadAsync(force: true).ConfigureAwait(true);
        StatusText = "Moment deleted.";
    }

    private async Task JumpAsync()
    {
        var selected = SelectedMoment;
        if (selected is null) return;
        await _playback.LoadAndPlayAtAsync(selected.Source.BroadcastId, selected.Source.PositionMs).ConfigureAwait(true);
        StatusText = $"Playing {selected.BroadcastText} from {selected.PositionText}.";
    }

    private void PlaybackOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlaybackViewModel.HasCurrentBroadcast) or nameof(PlaybackViewModel.PositionMs) or nameof(PlaybackViewModel.PositionText))
        {
            RaisePropertyChanged(nameof(CanCreateCurrent));
            RaisePropertyChanged(nameof(CreateCurrentText));
            RaiseCommandState();
        }
    }

    private void RaiseCommandState()
    {
        ((AsyncCommand)SearchCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)ClearSearchCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)CreateCurrentCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)SaveCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)DeleteCommand).RaiseCanExecuteChanged();
        ((AsyncCommand)JumpCommand).RaiseCanExecuteChanged();
    }

    private void SetError(Exception exception) => StatusText = $"Moment action failed: {exception.Message}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queryDebounce?.Cancel();
        _queryDebounce?.Dispose();
        _playback.PropertyChanged -= PlaybackOnPropertyChanged;
    }
}
