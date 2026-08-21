using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Server.ViewModels;

public sealed partial class ServerSettingsViewModel
{
    private RssFeedSubscription? _selectedRssFeed;
    private LibraryFolderRecord? _rssDestinationFolder;
    private string _rssFeedName = string.Empty;
    private string _rssFeedUrl = string.Empty;
    private string _rssUsername = string.Empty;
    private string _rssPassword = string.Empty;
    private string _rssIntervalMinutesText = "30";
    private string _rssStatusText = "Add a private or public RSS feed to watch for new broadcasts.";
    private bool _rssImportExisting;
    private bool _isRssBusy;
    private ServerCommand? _addRssFeedCommand;
    private ServerCommand? _toggleRssFeedCommand;
    private ServerCommand? _deleteRssFeedCommand;
    private ServerCommand? _checkSelectedRssFeedCommand;
    private ServerCommand? _checkAllRssFeedsCommand;

    public ObservableCollection<RssFeedSubscription> RssFeeds { get; } = new();
    public ObservableCollection<LibraryFolderRecord> RssDestinationFolders { get; } = new();
    public ICommand? AddRssFeedCommand { get; private set; }
    public ICommand? ToggleRssFeedCommand { get; private set; }
    public ICommand? DeleteRssFeedCommand { get; private set; }
    public ICommand? CheckSelectedRssFeedCommand { get; private set; }
    public ICommand? CheckAllRssFeedsCommand { get; private set; }

    public RssFeedSubscription? SelectedRssFeed
    {
        get => _selectedRssFeed;
        set
        {
            if (!Set(ref _selectedRssFeed, value)) return;
            RaiseRssCommandState();
        }
    }

    public LibraryFolderRecord? RssDestinationFolder
    {
        get => _rssDestinationFolder;
        set
        {
            if (!Set(ref _rssDestinationFolder, value)) return;
            RaiseRssCommandState();
        }
    }

    public string RssFeedName
    {
        get => _rssFeedName;
        set { if (Set(ref _rssFeedName, value)) RaiseRssCommandState(); }
    }

    public string RssFeedUrl
    {
        get => _rssFeedUrl;
        set { if (Set(ref _rssFeedUrl, value)) RaiseRssCommandState(); }
    }

    public string RssUsername { get => _rssUsername; set => Set(ref _rssUsername, value); }
    public string RssPassword { get => _rssPassword; set => Set(ref _rssPassword, value); }
    public string RssIntervalMinutesText { get => _rssIntervalMinutesText; set => Set(ref _rssIntervalMinutesText, value); }
    public bool RssImportExisting { get => _rssImportExisting; set => Set(ref _rssImportExisting, value); }
    public string RssStatusText { get => _rssStatusText; private set => Set(ref _rssStatusText, value); }
    public bool HasRssFeeds => RssFeeds.Count > 0;
    public bool HasRssDestinationFolders => RssDestinationFolders.Count > 0;
    public bool IsRssBusy
    {
        get => _isRssBusy;
        private set
        {
            if (!Set(ref _isRssBusy, value)) return;
            RaiseRssCommandState();
        }
    }

    private void InitializeRssFeedCommands()
    {
        _addRssFeedCommand = new ServerCommand(() => _ = AddRssFeedAsync(), CanAddRssFeed);
        _toggleRssFeedCommand = new ServerCommand(() => _ = ToggleRssFeedAsync(), () => !IsRssBusy && SelectedRssFeed is not null);
        _deleteRssFeedCommand = new ServerCommand(() => _ = DeleteRssFeedAsync(), () => !IsRssBusy && SelectedRssFeed is not null);
        _checkSelectedRssFeedCommand = new ServerCommand(
            () => _ = CheckRssFeedsAsync(SelectedRssFeed?.Id),
            () => !IsRssBusy && SelectedRssFeed?.Enabled == true && SelectedRssFeed.DestinationEnabled);
        _checkAllRssFeedsCommand = new ServerCommand(() => _ = CheckRssFeedsAsync(null), () => !IsRssBusy && RssFeeds.Any(value => value.Enabled && value.DestinationEnabled));
        AddRssFeedCommand = _addRssFeedCommand;
        ToggleRssFeedCommand = _toggleRssFeedCommand;
        DeleteRssFeedCommand = _deleteRssFeedCommand;
        CheckSelectedRssFeedCommand = _checkSelectedRssFeedCommand;
        CheckAllRssFeedsCommand = _checkAllRssFeedsCommand;
    }

    private async Task LoadRssFeedsAsync(long? selectId = null)
    {
        if (_runtime is null) return;
        try
        {
            var previousId = selectId ?? SelectedRssFeed?.Id;
            var feeds = await _runtime.GetRssFeedsAsync().ConfigureAwait(true);
            RssFeeds.Clear();
            foreach (var feed in feeds) RssFeeds.Add(feed);
            SelectedRssFeed = RssFeeds.FirstOrDefault(value => value.Id == previousId) ?? RssFeeds.FirstOrDefault();
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRssFeeds)));
            if (!IsRssBusy)
                RssStatusText = feeds.Count == 0
                    ? "No RSS feeds are configured. New subscriptions watch future episodes by default."
                    : $"{feeds.Count:N0} RSS feed{(feeds.Count == 1 ? string.Empty : "s")} configured.";
            RaiseRssCommandState();
        }
        catch (Exception exception) { RssStatusText = exception.Message; }
    }

    private void RefreshRssDestinationFolders()
    {
        var selectedId = RssDestinationFolder?.Id;
        RssDestinationFolders.Clear();
        foreach (var folder in LibraryFolders.Where(value => value.Enabled)) RssDestinationFolders.Add(folder);
        RssDestinationFolder = RssDestinationFolders.FirstOrDefault(value => value.IsManagedArchive)
                               ?? RssDestinationFolders.FirstOrDefault(value => value.Id == selectedId)
                               ?? RssDestinationFolders.FirstOrDefault();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasRssDestinationFolders)));
        RaiseRssCommandState();
    }

    private async Task AddRssFeedAsync()
    {
        if (_runtime is null || RssDestinationFolder is null || !CanAddRssFeed()) return;
        if (!int.TryParse(RssIntervalMinutesText, out var interval) || interval is < 5 or > 10080)
        {
            RssStatusText = "Check interval must be between 5 and 10,080 minutes (7 days).";
            return;
        }

        IsRssBusy = true;
        RssStatusText = "Saving and checking the RSS feed…";
        try
        {
            var created = await _runtime.AddRssFeedAsync(new RssFeedSaveRequest(
                RssFeedName,
                new RssFeedSource(RssFeedUrl, RssUsername, RssPassword),
                RssDestinationFolder.Id,
                interval,
                Enabled: true,
                ImportExistingOnFirstCheck: RssImportExisting,
                CollectionId: RssDestinationFolder.AssignedCollectionId)).ConfigureAwait(true);
            RssFeedName = string.Empty;
            RssFeedUrl = string.Empty;
            RssUsername = string.Empty;
            RssPassword = string.Empty;
            RssImportExisting = false;
            await LoadRssFeedsAsync(created.Id).ConfigureAwait(true);
            var result = await _runtime.CheckRssFeedsNowAsync(created.Id).ConfigureAwait(true);
            await LoadRssFeedsAsync(created.Id).ConfigureAwait(true);
            RssStatusText = result.Message;
        }
        catch (Exception exception) { RssStatusText = exception.Message; }
        finally { IsRssBusy = false; }
    }

    private async Task ToggleRssFeedAsync()
    {
        if (_runtime is null || SelectedRssFeed is null) return;
        var selected = SelectedRssFeed;
        IsRssBusy = true;
        try
        {
            await _runtime.SetRssFeedEnabledAsync(selected.Id, !selected.Enabled).ConfigureAwait(true);
            await LoadRssFeedsAsync(selected.Id).ConfigureAwait(true);
            RssStatusText = selected.Enabled ? "RSS feed paused." : "RSS feed enabled and ready to check.";
        }
        catch (Exception exception) { RssStatusText = exception.Message; }
        finally { IsRssBusy = false; }
    }

    private async Task DeleteRssFeedAsync()
    {
        if (_runtime is null || SelectedRssFeed is null) return;
        var selected = SelectedRssFeed;
        IsRssBusy = true;
        try
        {
            await _runtime.DeleteRssFeedAsync(selected.Id).ConfigureAwait(true);
            SelectedRssFeed = null;
            await LoadRssFeedsAsync().ConfigureAwait(true);
            RssStatusText = "RSS subscription removed. Downloaded broadcasts remain in the Library.";
        }
        catch (Exception exception) { RssStatusText = exception.Message; }
        finally { IsRssBusy = false; }
    }

    private async Task CheckRssFeedsAsync(long? feedId)
    {
        if (_runtime is null) return;
        IsRssBusy = true;
        RssStatusText = feedId.HasValue ? "Checking the selected RSS feed…" : "Checking all enabled RSS feeds…";
        try
        {
            var result = await _runtime.CheckRssFeedsNowAsync(feedId).ConfigureAwait(true);
            await LoadRssFeedsAsync(feedId).ConfigureAwait(true);
            RssStatusText = result.Message;
        }
        catch (Exception exception) { RssStatusText = exception.Message; }
        finally { IsRssBusy = false; }
    }

    private bool CanAddRssFeed()
        => !IsRssBusy && RssDestinationFolder is not null &&
           !string.IsNullOrWhiteSpace(RssFeedName) && !string.IsNullOrWhiteSpace(RssFeedUrl);

    private void RaiseRssCommandState()
    {
        _addRssFeedCommand?.RaiseCanExecuteChanged();
        _toggleRssFeedCommand?.RaiseCanExecuteChanged();
        _deleteRssFeedCommand?.RaiseCanExecuteChanged();
        _checkSelectedRssFeedCommand?.RaiseCanExecuteChanged();
        _checkAllRssFeedsCommand?.RaiseCanExecuteChanged();
    }
}
