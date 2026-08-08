using System.ComponentModel;
using TheRadioVault.Presentation.Infrastructure;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class ShellActivityViewModel : ObservableObject, IDisposable
{
    private readonly DashboardViewModel _dashboard;
    private readonly LibraryViewModel _library;
    private readonly SearchViewModel _search;
    private readonly QueueViewModel _queue;
    private readonly MomentsViewModel _moments;
    private readonly TranscriptsViewModel _transcripts;
    private readonly ResearchWorkspaceViewModel _research;
    private readonly DownloadsViewModel _downloads;
    private readonly PlaybackViewModel _playback;
    private readonly NowPlayingViewModel _nowPlaying;
    private readonly FullBroadcastInfoViewModel _broadcastInfo;
    private readonly DesktopToolsViewModel _tools;
    private readonly INotifyPropertyChanged[] _sources;
    private bool _isVisible;
    private string _title = string.Empty;
    private string _detail = string.Empty;
    private bool _hasProgress;
    private double _progressPercent;

    public ShellActivityViewModel(
        DashboardViewModel dashboard,
        LibraryViewModel library,
        SearchViewModel search,
        QueueViewModel queue,
        MomentsViewModel moments,
        TranscriptsViewModel transcripts,
        ResearchWorkspaceViewModel research,
        DownloadsViewModel downloads,
        PlaybackViewModel playback,
        NowPlayingViewModel nowPlaying,
        FullBroadcastInfoViewModel broadcastInfo,
        DesktopToolsViewModel tools)
    {
        _dashboard = dashboard ?? throw new ArgumentNullException(nameof(dashboard));
        _library = library ?? throw new ArgumentNullException(nameof(library));
        _search = search ?? throw new ArgumentNullException(nameof(search));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _moments = moments ?? throw new ArgumentNullException(nameof(moments));
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _research = research ?? throw new ArgumentNullException(nameof(research));
        _downloads = downloads ?? throw new ArgumentNullException(nameof(downloads));
        _playback = playback ?? throw new ArgumentNullException(nameof(playback));
        _nowPlaying = nowPlaying ?? throw new ArgumentNullException(nameof(nowPlaying));
        _broadcastInfo = broadcastInfo ?? throw new ArgumentNullException(nameof(broadcastInfo));
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));

        _sources =
        [
            _dashboard,
            _library,
            _search,
            _queue,
            _moments,
            _transcripts,
            _research,
            _downloads,
            _playback,
            _nowPlaying,
            _broadcastInfo,
            _tools
        ];

        foreach (var source in _sources)
            source.PropertyChanged += OnSourcePropertyChanged;

        Refresh();
    }

    public bool IsVisible { get => _isVisible; private set => SetProperty(ref _isVisible, value); }
    public string Title { get => _title; private set => SetProperty(ref _title, value); }
    public string Detail { get => _detail; private set => SetProperty(ref _detail, value); }
    public bool HasProgress { get => _hasProgress; private set => SetProperty(ref _hasProgress, value); }
    public bool IsIndeterminate => IsVisible && !HasProgress;
    public double ProgressPercent { get => _progressPercent; private set => SetProperty(ref _progressPercent, Math.Clamp(value, 0, 100)); }
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail)
        && !string.Equals(Title, Detail, StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        foreach (var source in _sources)
            source.PropertyChanged -= OnSourcePropertyChanged;
    }

    private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
        => Refresh();

    private void Refresh()
    {
        var activity = ResolveActivity();
        IsVisible = activity is not null;
        Title = activity?.Title ?? string.Empty;
        Detail = activity?.Detail ?? string.Empty;
        HasProgress = activity?.ProgressPercent.HasValue == true;
        ProgressPercent = activity?.ProgressPercent ?? 0;
        RaisePropertyChanged(nameof(HasDetail));
        RaisePropertyChanged(nameof(IsIndeterminate));
    }

    private ShellActivity? ResolveActivity()
    {
        if (_transcripts.IsTranscriptionActive)
            return Create("Transcribing locally", _transcripts.ActivityText, "Processing audio with the configured local Whisper worker…");

        if (_tools.IsScanRunning || (_tools.IsBusy && IsLibraryScanStatus(_tools.StatusText)))
            return Create("Scanning Library", _tools.StatusText, "Checking registered archive folders and updating the Library…");

        if (_research.IsImportBusy)
        {
            var title = ContainsAny(_research.StatusText, "checking", "preview")
                ? "Checking research pack"
                : "Importing research";
            return Create(title, _research.StatusText, "Updating Knowledge and protected metadata…", _research.ImportProgressPercent);
        }

        if (_research.IsBusy)
        {
            var title = ContainsAny(_research.StatusText, "export")
                ? "Exporting research"
                : ContainsAny(_research.StatusText, "saving metadata", "metadata saved")
                    ? "Saving metadata"
                    : "Updating Research";
            return Create(title, _research.StatusText, "Refreshing Research records and coverage…");
        }

        if (_tools.IsBusy)
        {
            var title = ResolveToolsTitle(_tools.StatusText);
            return Create(title, _tools.StatusText, "Applying archive and application settings…");
        }

        if (_downloads.IsDownloading)
            return Create("Downloading broadcast", _downloads.ActiveDetail, "Saving the canonical recording on this PC…", _downloads.DownloadPercent);

        if (_downloads.IsBusy)
            return Create("Checking downloads", _downloads.StatusText, "Checking media stored on this PC…");

        if (_playback.IsPrimaryTransportLoading)
            return Create("Preparing playback", _playback.StatusText, "Opening the selected audio…");

        if (_library.IsBusy)
            return Create("Loading Library", _library.StatusText, "Refreshing broadcasts and listening state…");

        if (_dashboard.IsBusy)
            return Create("Refreshing Dashboard", _dashboard.StatusText, "Updating listening suggestions and archive highlights…");

        if (_search.IsBusy)
        {
            var title = ContainsAny(_search.StatusText, "searching") ? "Searching archive" : "Loading Search";
            return Create(title, _search.StatusText, "Finding matching broadcasts…");
        }

        if (_nowPlaying.IsBusy)
            return Create("Loading Now Playing", _nowPlaying.ActivityText, "Loading broadcast context and queue details…");

        if (_broadcastInfo.IsBusy)
            return Create("Loading broadcast information", _broadcastInfo.StatusText, "Gathering metadata and archive details…");

        if (_moments.IsBusy)
            return Create("Updating Moments", _moments.StatusText, "Refreshing saved listening points…");

        if (_queue.IsBusy)
            return Create("Updating queue", _queue.StatusText, "Refreshing what plays next…");

        return null;
    }

    private static ShellActivity Create(string title, string? detail, string fallbackDetail, double? progressPercent = null)
    {
        var normalizedDetail = string.IsNullOrWhiteSpace(detail)
            ? fallbackDetail
            : detail.Trim();
        return new ShellActivity(title, normalizedDetail, progressPercent);
    }

    private static string ResolveToolsTitle(string? status)
    {
        if (ContainsAny(status, "restoring")) return "Restoring backup";
        if (ContainsAny(status, "backup")) return "Creating backup";
        if (ContainsAny(status, "health", "analys")) return "Analysing archive";
        if (ContainsAny(status, "anywhere", "certificate", "private link")) return "Updating Radio Vault Web";
        if (ContainsAny(status, "diagnostic", "stress test", "quick test")) return "Running diagnostics";
        if (ContainsAny(status, "transcription", "whisper")) return "Updating transcription settings";
        if (ContainsAny(status, "folder")) return "Updating Library folders";
        return "Updating Settings";
    }

    private static bool IsLibraryScanStatus(string? status)
        => ContainsAny(status, "scanning registered archive folders", "scanning library", "library scan");

    private static bool ContainsAny(string? value, params string[] fragments)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return fragments.Any(fragment => value.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record ShellActivity(string Title, string Detail, double? ProgressPercent);
}
