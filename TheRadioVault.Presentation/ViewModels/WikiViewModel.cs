using System.Collections.ObjectModel;
using System.Windows.Input;
using TheRadioVault.Application.Abstractions;
using TheRadioVault.Application.Models;
using TheRadioVault.Core.Domain;
using TheRadioVault.Presentation.Infrastructure;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Services.Services;

namespace TheRadioVault.Presentation.ViewModels;

public sealed class WikiViewModel : ObservableObject
{
    private readonly IWikiService _wiki;
    private readonly IWikiPackTransferService _packs;
    private readonly IFileSelectionService _files;
    private Func<long, Task>? _openBroadcastInfo;
    private Func<ArchiveEntityLink, Task>? _openEntityLink;
    private readonly AsyncCommand _saveCommand;
    private readonly AsyncCommand _revertCommand;
    private readonly AsyncCommand _applyImportCommand;
    private readonly AsyncCommand _cancelImportCommand;
    private readonly AsyncCommand _chooseImportCommand;
    private readonly AsyncCommand _exportCommand;
    private readonly AsyncCommand _generateStartersCommand;
    private readonly AsyncCommand _saveCitationCommand;
    private readonly AsyncCommand _removeCitationCommand;
    private readonly AsyncCommand _chooseImageCommand;
    private readonly AsyncCommand _saveImageCommand;
    private readonly AsyncCommand _removeImageCommand;
    private readonly AsyncCommand _saveTimelineCommand;
    private readonly AsyncCommand _removeTimelineCommand;
    private readonly AsyncCommand _searchArchiveCommand;
    private bool _isBusy;
    private bool _isLoaded;
    private string _statusText = "Ready";
    private string _searchText = string.Empty;
    private string _selectedTypeFilter = "All types";
    private string _selectedStatusFilter = "All statuses";
    private WikiOverview? _overview;
    private WikiPageSummary? _selectedPage;
    private WikiPageDocument? _document;
    private WikiPackPreview? _importPreview;
    private int _selectionVersion;
    private Guid? _editorPageId;
    private int _editorRevision;
    private string _editorTitle = string.Empty;
    private string _editorSlug = string.Empty;
    private string _editorPageType = "Show";
    private string _editorStatus = "Draft";
    private string _editorSummary = string.Empty;
    private string _editorBodyMarkdown = string.Empty;
    private string _editorAliases = string.Empty;
    private string _editorChangeSummary = string.Empty;
    private WikiStarterPagePreview? _starterPreview;
    private WikiCitationRecord? _selectedCitation;
    private WikiImageDraft? _selectedImage;
    private WikiTimelineEventRecord? _selectedTimelineEvent;
    private string _citationSourceTitle = string.Empty;
    private string _citationSourceType = "Web";
    private string _citationPublisher = string.Empty;
    private string _citationAuthor = string.Empty;
    private string _citationUrl = string.Empty;
    private string _citationPublishedDate = string.Empty;
    private string _citationLocator = string.Empty;
    private string _citationQuotedText = string.Empty;
    private string _citationNote = string.Empty;
    private string _imageCaption = string.Empty;
    private string _imageAltText = string.Empty;
    private string _imageCreator = string.Empty;
    private string _imageCopyright = string.Empty;
    private string _imageLicence = string.Empty;
    private string _imageCapturedDate = string.Empty;
    private string _imageRepresentativeFrom = string.Empty;
    private string _imageRepresentativeTo = string.Empty;
    private string _imageDateNotes = string.Empty;
    private string _imageRole = "Article";
    private string _imageFileName = string.Empty;
    private byte[]? _pendingImageBytes;
    private string _timelineTitle = string.Empty;
    private string _timelineSummary = string.Empty;
    private string _timelineCategory = "Milestone";
    private string _timelineStartDate = string.Empty;
    private string _timelineEndDate = string.Empty;
    private string _timelineDateDisplay = string.Empty;
    private string _timelineSignificance = "50";
    private string _archiveSearchText = string.Empty;
    private WikiArchiveBroadcastCandidate? _selectedArchiveBroadcast;
    private WikiArchiveMomentCandidate? _selectedArchiveMoment;
    private WikiTimelineBroadcastLink? _selectedTimelineLink;
    private bool _isReadingMode = true;
    private bool _isDashboardMode = true;
    private bool _isBrowseMode;
    private bool _isTimelineExplorerMode;
    private WikiRevisionRecord? _selectedRevision;
    private WikiCitationAuditReport? _citationAudit;
    private WikiQualityAuditReport? _qualityAudit;
    private TopicCleanupReport? _topicCleanup;
    private TopicMergeSuggestion? _selectedTopicSuggestion;
    private string _revisionComparisonText = string.Empty;
    private readonly List<WikiPageSummary> _allPages = new();
    private readonly Stack<WikiNavigationEntry> _backHistory = new();
    private readonly Stack<WikiNavigationEntry> _forwardHistory = new();
    private WikiPageSummary? _selectedSearchSuggestion;
    private WikiTimelineShowSummary? _selectedTimelineShow;
    private double _timelineYear;
    private int _timelineMinimumYear = DateTime.Today.Year;
    private int _timelineMaximumYear = DateTime.Today.Year;

    public WikiViewModel(IWikiService wiki, IWikiPackTransferService packs, IFileSelectionService files, PlaybackViewModel? playback = null)
    {
        _wiki = wiki ?? throw new ArgumentNullException(nameof(wiki));
        _packs = packs ?? throw new ArgumentNullException(nameof(packs));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _ = playback;

        TypeFilters = new ObservableCollection<string>(new[] { "All types" }.Concat(WikiPageTypes.All));
        StatusFilters = new ObservableCollection<string>(new[] { "All statuses" }.Concat(WikiPageStatuses.All));
        PageTypes = new ObservableCollection<string>(WikiPageTypes.All);
        PageStatuses = new ObservableCollection<string>(WikiPageStatuses.All);

        RefreshCommand = new AsyncCommand(() => LoadAsync(force: true), onError: SetError);
        SearchCommand = new AsyncCommand(LoadPagesAsync, () => !IsBusy, SetError);
        ClearFiltersCommand = new AsyncCommand(ClearFiltersAsync, () => !IsBusy, SetError);
        NewPageCommand = new AsyncCommand(NewPageAsync, () => !IsBusy, SetError);
        _saveCommand = new AsyncCommand(SaveAsync, () => CanSave, SetError);
        _revertCommand = new AsyncCommand(RevertAsync, () => HasExistingPage && !IsBusy, SetError);
        _chooseImportCommand = new AsyncCommand(ChooseImportAsync, () => _packs.IsAvailable && !IsBusy, SetError);
        _applyImportCommand = new AsyncCommand(ApplyImportAsync, () => HasPendingImport && !IsBusy, SetError);
        _cancelImportCommand = new AsyncCommand(CancelImportAsync, () => HasPendingImport && !IsBusy, SetError);
        _exportCommand = new AsyncCommand(ExportAsync, () => _packs.IsAvailable && !IsBusy, SetError);
        _generateStartersCommand = new AsyncCommand(GenerateStartersAsync, () => !IsBusy && StarterPreview?.NewPageCount > 0, SetError);
        _saveCitationCommand = new AsyncCommand(SaveCitationAsync, () => HasExistingPage && !IsBusy && !string.IsNullOrWhiteSpace(CitationSourceTitle), SetError);
        _removeCitationCommand = new AsyncCommand(RemoveCitationAsync, () => SelectedCitation is not null && !IsBusy, SetError);
        _chooseImageCommand = new AsyncCommand(ChooseImageAsync, () => HasExistingPage && !IsBusy, SetError);
        _saveImageCommand = new AsyncCommand(SaveImageAsync, () => HasExistingPage && !IsBusy && (!string.IsNullOrWhiteSpace(ImageFileName) || SelectedImage is not null), SetError);
        _removeImageCommand = new AsyncCommand(RemoveImageAsync, () => SelectedImage is not null && !IsBusy, SetError);
        _saveTimelineCommand = new AsyncCommand(SaveTimelineAsync, () => HasExistingPage && !IsBusy && !string.IsNullOrWhiteSpace(TimelineTitle), SetError);
        _removeTimelineCommand = new AsyncCommand(RemoveTimelineAsync, () => SelectedTimelineEvent is not null && !IsBusy, SetError);
        _searchArchiveCommand = new AsyncCommand(SearchArchiveAsync, () => !IsBusy, SetError);
        SaveCommand = _saveCommand;
        RevertCommand = _revertCommand;
        ChooseImportCommand = _chooseImportCommand;
        ApplyImportCommand = _applyImportCommand;
        CancelImportCommand = _cancelImportCommand;
        ExportCommand = _exportCommand;
        GenerateStartersCommand = _generateStartersCommand;
        SaveCitationCommand = _saveCitationCommand;
        RemoveCitationCommand = _removeCitationCommand;
        ChooseImageCommand = _chooseImageCommand;
        SaveImageCommand = _saveImageCommand;
        RemoveImageCommand = _removeImageCommand;
        SaveTimelineCommand = _saveTimelineCommand;
        RemoveTimelineCommand = _removeTimelineCommand;
        SearchArchiveCommand = _searchArchiveCommand;
        NewCitationCommand = new AsyncCommand(NewCitationAsync, onError: SetError);
        NewImageCommand = new AsyncCommand(NewImageAsync, onError: SetError);
        NewTimelineCommand = new AsyncCommand(NewTimelineAsync, onError: SetError);
        AddBroadcastLinkCommand = new AsyncCommand(AddBroadcastLinkAsync, onError: SetError);
        AddMomentLinkCommand = new AsyncCommand(AddMomentLinkAsync, onError: SetError);
        RemoveTimelineLinkCommand = new AsyncCommand(RemoveTimelineLinkAsync, onError: SetError);
        ShowReadingModeCommand = new AsyncCommand(ShowReadingModeAsync, onError: SetError);
        ShowEditingModeCommand = new AsyncCommand(ShowEditingModeAsync, onError: SetError);
        ShowDashboardCommand = new AsyncCommand(ShowDashboardAsync, onError: SetError);
        BrowseWikiCommand = new AsyncCommand(BrowseWikiAsync, onError: SetError);
        ManageWikiCommand = new AsyncCommand(ManageWikiAsync, onError: SetError);
        RandomPageCommand = new AsyncCommand(OpenRandomPageAsync, onError: SetError);
        BackCommand = new AsyncCommand(NavigateBackAsync, () => CanGoBack && !IsBusy, SetError);
        ForwardCommand = new AsyncCommand(NavigateForwardAsync, () => CanGoForward && !IsBusy, SetError);
        ShowTimelineExplorerCommand = new AsyncCommand(ShowTimelineExplorerAsync, onError: SetError);
        OpenInternalWikiLinkCommand = new DelegateCommand(parameter => _ = OpenInternalWikiLinkAsync(parameter?.ToString()));
        OpenPageCommand = new DelegateCommand(parameter => { if (parameter is WikiPageSummary page) _ = OpenPageAsync(page, true); });
        OpenEraCommand = new DelegateCommand(parameter => { if (parameter is WikiEraSummary era) _ = OpenEraAsync(era); });
        OpenTimelineEventPageCommand = new DelegateCommand(parameter => { if (parameter is WikiTimelineExplorerEvent item) _ = OpenPageAsync(item.Page, true); });
        PlayTimelineLinkCommand = new DelegateCommand(parameter => _ = PlayTimelineLinkAsync(parameter as WikiTimelineBroadcastLink));
        OpenCitationArchiveLinkCommand = new DelegateCommand(parameter => _ = OpenCitationArchiveLinkAsync(parameter as WikiCitationRecord));
        RestoreRevisionCommand = new AsyncCommand(RestoreSelectedRevisionAsync, () => SelectedRevision is not null && !IsBusy, SetError);
        AuditCitationsCommand = new AsyncCommand(AuditCitationsAsync, () => !IsBusy, SetError);
        AuditQualityCommand = new AsyncCommand(AuditQualityAsync, () => !IsBusy, SetError);
        AuditTopicsCommand = new AsyncCommand(AuditTopicsAsync, () => !IsBusy, SetError);
        AutomaticTopicCleanupCommand = new AsyncCommand(RunAutomaticTopicCleanupAsync, () => !IsBusy, SetError);
        MergeSelectedTopicCommand = new AsyncCommand(MergeSelectedTopicAsync, () => SelectedTopicSuggestion is not null && !IsBusy, SetError);
        MergeTopicSuggestionCommand = new DelegateCommand(parameter => { if (parameter is TopicMergeSuggestion suggestion) _ = MergeTopicSuggestionAsync(suggestion); });
    }

    public ObservableCollection<WikiPageSummary> Pages { get; } = new();
    public ObservableCollection<WikiCitationRecord> Citations { get; } = new();
    public ObservableCollection<WikiPageImageLink> Images { get; } = new();
    public ObservableCollection<WikiTimelineEventRecord> Timeline { get; } = new();
    public ObservableCollection<WikiImageDraft> ImageDrafts { get; } = new();
    public ObservableCollection<WikiPackPageChangePreview> ImportChanges { get; } = new();
    public ObservableCollection<WikiArchiveBroadcastCandidate> ArchiveBroadcasts { get; } = new();
    public ObservableCollection<WikiArchiveMomentCandidate> ArchiveMoments { get; } = new();
    public ObservableCollection<WikiPageSummary> FeaturedPages { get; } = new();
    public ObservableCollection<WikiPageSummary> ShowPages { get; } = new();
    public ObservableCollection<WikiPageSummary> PeoplePages { get; } = new();
    public ObservableCollection<WikiPageSummary> TopicPages { get; } = new();
    public ObservableCollection<WikiPageSummary> RecentPages { get; } = new();
    public ObservableCollection<WikiPageSummary> TimelinePages { get; } = new();
    public ObservableCollection<WikiReaderImageViewModel> ReaderImages { get; } = new();
    public ObservableCollection<WikiRevisionRecord> Revisions { get; } = new();
    public ObservableCollection<WikiCitationAuditIssue> CitationAuditIssues { get; } = new();
    public ObservableCollection<WikiPageSummary> SearchSuggestions { get; } = new();
    public ObservableCollection<string> InlineLinkTargets { get; } = new();
    public ObservableCollection<WikiPageSummary> RelatedPages { get; } = new();
    public ObservableCollection<WikiPageSummary> BacklinkPages { get; } = new();
    public ObservableCollection<WikiMissingLink> MissingLinks { get; } = new();
    public ObservableCollection<WikiOnThisDayItem> OnThisDayItems { get; } = new();
    public ObservableCollection<WikiEraSummary> Eras { get; } = new();
    public ObservableCollection<WikiTimelineShowSummary> TimelineShows { get; } = new();
    public ObservableCollection<WikiTimelineExplorerEvent> TimelineExplorerEvents { get; } = new();

    public void SetOpenBroadcastInfoHandler(Func<long, Task> handler)
        => _openBroadcastInfo = handler ?? throw new ArgumentNullException(nameof(handler));
    public void SetOpenEntityLinkHandler(Func<ArchiveEntityLink, Task> handler)
        => _openEntityLink = handler ?? throw new ArgumentNullException(nameof(handler));
    public ObservableCollection<WikiTimelineExplorerEvent> FilteredTimelineEvents { get; } = new();
    public ObservableCollection<WikiTimelineBroadcastLink> TimelineLinks { get; } = new();
    public ObservableCollection<WikiPageSummary> OrphanPages { get; } = new();
    public ObservableCollection<WikiMissingLink> BrokenLinks { get; } = new();
    public ObservableCollection<WikiDuplicatePageCandidate> DuplicatePages { get; } = new();
    public ObservableCollection<TopicMergeSuggestion> TopicMergeSuggestions { get; } = new();
    public ObservableCollection<TopicMergeHistoryRecord> TopicMergeHistory { get; } = new();
    public ObservableCollection<string> TypeFilters { get; }
    public ObservableCollection<string> StatusFilters { get; }
    public ObservableCollection<string> PageTypes { get; }
    public ObservableCollection<string> PageStatuses { get; }

    public ICommand RefreshCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand ClearFiltersCommand { get; }
    public ICommand NewPageCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand RevertCommand { get; }
    public ICommand ChooseImportCommand { get; }
    public ICommand ApplyImportCommand { get; }
    public ICommand CancelImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand GenerateStartersCommand { get; }
    public ICommand NewCitationCommand { get; }
    public ICommand SaveCitationCommand { get; }
    public ICommand RemoveCitationCommand { get; }
    public ICommand NewImageCommand { get; }
    public ICommand ChooseImageCommand { get; }
    public ICommand SaveImageCommand { get; }
    public ICommand RemoveImageCommand { get; }
    public ICommand NewTimelineCommand { get; }
    public ICommand SaveTimelineCommand { get; }
    public ICommand RemoveTimelineCommand { get; }
    public ICommand SearchArchiveCommand { get; }
    public ICommand AddBroadcastLinkCommand { get; }
    public ICommand AddMomentLinkCommand { get; }
    public ICommand RemoveTimelineLinkCommand { get; }
    public ICommand ShowReadingModeCommand { get; }
    public ICommand ShowEditingModeCommand { get; }
    public ICommand ShowDashboardCommand { get; }
    public ICommand BrowseWikiCommand { get; }
    public ICommand ManageWikiCommand { get; }
    public ICommand RandomPageCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand ForwardCommand { get; }
    public ICommand ShowTimelineExplorerCommand { get; }
    public ICommand OpenInternalWikiLinkCommand { get; }
    public ICommand OpenPageCommand { get; }
    public ICommand OpenEraCommand { get; }
    public ICommand OpenTimelineEventPageCommand { get; }
    public ICommand PlayTimelineLinkCommand { get; }
    public ICommand OpenCitationArchiveLinkCommand { get; }
    public ICommand RestoreRevisionCommand { get; }
    public ICommand AuditCitationsCommand { get; }
    public ICommand AuditQualityCommand { get; }
    public ICommand AuditTopicsCommand { get; }
    public ICommand AutomaticTopicCleanupCommand { get; }
    public ICommand MergeSelectedTopicCommand { get; }
    public ICommand MergeTopicSuggestionCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RaisePropertyChanged(nameof(CanSave));
            RaiseCommandState();
        }
    }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            UpdateSearchSuggestions();
        }
    }
    public WikiPageSummary? SelectedSearchSuggestion
    {
        get => _selectedSearchSuggestion;
        set
        {
            if (!SetProperty(ref _selectedSearchSuggestion, value) || value is null) return;
            _ = OpenPageAsync(value, true);
        }
    }
    public string SelectedTypeFilter { get => _selectedTypeFilter; set => SetProperty(ref _selectedTypeFilter, value); }
    public string SelectedStatusFilter { get => _selectedStatusFilter; set => SetProperty(ref _selectedStatusFilter, value); }
    public WikiOverview? Overview { get => _overview; private set { if (SetProperty(ref _overview, value)) { RaisePropertyChanged(nameof(OverviewText)); RaisePropertyChanged(nameof(DashboardIntroduction)); } } }
    public string OverviewText => Overview is null
        ? "Loading wiki…"
        : $"{Overview.PageCount:N0} pages · {Overview.SourceCount:N0} sources · {Overview.ImageCount:N0} images · {Overview.TimelineEventCount:N0} timeline events";
    public WikiPageSummary? SelectedPage
    {
        get => _selectedPage;
        set
        {
            if (value is null || value.PageId == _selectedPage?.PageId) return;
            _ = OpenPageAsync(value, true);
        }
    }
    public WikiPackPreview? ImportPreview
    {
        get => _importPreview;
        private set
        {
            if (!SetProperty(ref _importPreview, value)) return;
            RaisePropertyChanged(nameof(HasPendingImport));
            RaisePropertyChanged(nameof(ImportPreviewText));
            ImportChanges.Clear();
            foreach (var change in value?.PageChanges ?? Array.Empty<WikiPackPageChangePreview>()) ImportChanges.Add(change);
            RaiseCommandState();
        }
    }

    public WikiStarterPagePreview? StarterPreview
    {
        get => _starterPreview;
        private set
        {
            if (!SetProperty(ref _starterPreview, value)) return;
            RaisePropertyChanged(nameof(StarterPreviewText));
            RaisePropertyChanged(nameof(HasStarterPages));
            RaiseCommandState();
        }
    }
    public string StarterPreviewText => StarterPreview?.Summary ?? "Checking the archive for starter pages…";
    public bool HasStarterPages => StarterPreview?.NewPageCount > 0;

    public string EditorTitle { get => _editorTitle; set { if (SetProperty(ref _editorTitle, value)) { RaiseEditorState(); RaiseReaderState(); } } }
    public string EditorSlug { get => _editorSlug; set => SetProperty(ref _editorSlug, value); }
    public string EditorPageType { get => _editorPageType; set => SetProperty(ref _editorPageType, value); }
    public string EditorStatus { get => _editorStatus; set => SetProperty(ref _editorStatus, value); }
    public string EditorSummary { get => _editorSummary; set { if (SetProperty(ref _editorSummary, value)) RaiseReaderState(); } }
    public string EditorBodyMarkdown { get => _editorBodyMarkdown; set { if (SetProperty(ref _editorBodyMarkdown, value)) RaiseReaderState(); } }
    public string EditorAliases { get => _editorAliases; set => SetProperty(ref _editorAliases, value); }
    public string EditorChangeSummary { get => _editorChangeSummary; set => SetProperty(ref _editorChangeSummary, value); }
    public string EditorRevisionText => HasExistingPage ? $"Revision {_editorRevision:N0}" : "New page";
    public bool HasPages => Pages.Count > 0;
    public bool HasNoPages => Pages.Count == 0;
    public bool HasSelection => _editorPageId.HasValue || !string.IsNullOrWhiteSpace(EditorTitle);
    public bool HasExistingPage => _editorPageId.HasValue;
    public bool CanSave => !IsBusy && !string.IsNullOrWhiteSpace(EditorTitle);
    public bool HasCitations => Citations.Count > 0;
    public bool HasImages => Images.Count > 0;
    public bool HasTimeline => Timeline.Count > 0;
    public bool HasPendingImport => ImportPreview?.CanApply == true;
    public string ImportPreviewText => ImportPreview?.Summary ?? string.Empty;
    public string SelectionHeading => string.IsNullOrWhiteSpace(EditorTitle) ? "Choose or create a page" : EditorTitle;
    public bool IsReadingMode { get => _isReadingMode; private set { if (SetProperty(ref _isReadingMode, value)) { RaisePropertyChanged(nameof(IsEditingMode)); RaiseModeState(); } } }
    public bool IsEditingMode => !IsReadingMode;
    public bool IsDashboardMode { get => _isDashboardMode; private set { if (SetProperty(ref _isDashboardMode, value)) RaiseModeState(); } }
    public bool IsBrowseMode { get => _isBrowseMode; private set { if (SetProperty(ref _isBrowseMode, value)) RaiseModeState(); } }
    public bool IsTimelineExplorerMode { get => _isTimelineExplorerMode; private set { if (SetProperty(ref _isTimelineExplorerMode, value)) RaiseModeState(); } }
    public bool IsExplorerMode => !IsDashboardMode;
    public bool IsArticleMode => !IsDashboardMode && !IsBrowseMode && !IsTimelineExplorerMode;
    public bool CanGoBack => _backHistory.Count > 0;
    public bool CanGoForward => _forwardHistory.Count > 0;
    public string ReaderBodyText => FormatMarkdownForReading(EditorBodyMarkdown);
    public string ReaderByline => _document is null ? "Unsaved draft" : $"{EditorPageType} · {EditorStatus} · revision {_editorRevision:N0} · last edited by {_document.LastEditor}";
    public string DashboardIntroduction => Overview is null
        ? "Loading the story of the archive…"
        : $"Explore {Overview.PageCount:N0} articles, {Overview.TimelineEventCount:N0} dated events, {Overview.SourceCount:N0} sources and {Overview.ImageCount:N0} historical images.";
    public bool HasFeaturedPages => FeaturedPages.Count > 0;
    public bool HasShowPages => ShowPages.Count > 0;
    public bool HasPeoplePages => PeoplePages.Count > 0;
    public bool HasTopicPages => TopicPages.Count > 0;
    public bool HasRecentPages => RecentPages.Count > 0;
    public bool HasTimelinePages => TimelinePages.Count > 0;
    public bool HasReaderImages => ReaderImages.Count > 0;
    public bool HasRevisions => Revisions.Count > 0;
    public WikiRevisionRecord? SelectedRevision
    {
        get => _selectedRevision;
        set
        {
            if (!SetProperty(ref _selectedRevision, value)) return;
            RevisionComparisonText = BuildRevisionComparison(value, _document);
            if (RestoreRevisionCommand is AsyncCommand restore) restore.RaiseCanExecuteChanged();
        }
    }
    public string RevisionComparisonText { get => _revisionComparisonText; private set => SetProperty(ref _revisionComparisonText, value); }
    public WikiCitationAuditReport? CitationAudit { get => _citationAudit; private set { if (SetProperty(ref _citationAudit, value)) RaisePropertyChanged(nameof(CitationAuditSummary)); } }
    public string CitationAuditSummary => CitationAudit?.Summary ?? "Run a citation audit after importing or revising the Explore baseline.";
    public bool HasCitationAuditIssues => CitationAuditIssues.Count > 0;
    public WikiQualityAuditReport? QualityAudit { get => _qualityAudit; private set { if (SetProperty(ref _qualityAudit, value)) RaisePropertyChanged(nameof(QualityAuditSummary)); } }
    public string QualityAuditSummary => QualityAudit?.Summary ?? "Check citations, inline links, duplicate pages and disconnected articles.";
    public bool HasRelatedPages => RelatedPages.Count > 0;
    public bool HasBacklinks => BacklinkPages.Count > 0;
    public bool HasMissingLinks => MissingLinks.Count > 0;
    public bool HasOnThisDay => OnThisDayItems.Count > 0;
    public bool HasEras => Eras.Count > 0;
    public bool HasOrphans => OrphanPages.Count > 0;
    public bool HasBrokenLinks => BrokenLinks.Count > 0;
    public bool HasDuplicates => DuplicatePages.Count > 0;
    public TopicCleanupReport? TopicCleanup { get => _topicCleanup; private set { if (SetProperty(ref _topicCleanup, value)) RaisePropertyChanged(nameof(TopicCleanupSummary)); } }
    public string TopicCleanupSummary => TopicCleanup?.Summary ?? "Safe naming differences are merged automatically; meaning-based matches wait for review.";
    public bool HasTopicSuggestions => TopicMergeSuggestions.Count > 0;
    public TopicMergeSuggestion? SelectedTopicSuggestion
    {
        get => _selectedTopicSuggestion;
        set
        {
            if (!SetProperty(ref _selectedTopicSuggestion, value)) return;
            if (MergeSelectedTopicCommand is AsyncCommand command) command.RaiseCanExecuteChanged();
        }
    }
    public string ReaderAliases => _document is null || _document.Aliases.Count == 0 ? "None recorded" : string.Join(", ", _document.Aliases);
    public string ReaderEvidence => $"{Citations.Count:N0} citations | {Images.Count:N0} images | {Timeline.Count:N0} timeline events";
    public string ReaderTimelineRange
    {
        get
        {
            var years = Timeline.Where(x => x.StartDate.HasValue).Select(x => x.StartDate!.Value.Year).ToArray();
            return years.Length == 0 ? "No dated events" : years.Min() == years.Max() ? years.Min().ToString() : $"{years.Min()}-{years.Max()}";
        }
    }

    public WikiTimelineShowSummary? SelectedTimelineShow
    {
        get => _selectedTimelineShow;
        set
        {
            if (!SetProperty(ref _selectedTimelineShow, value) || value is null) return;
            _ = LoadTimelineShowAsync(value);
        }
    }
    public int TimelineMinimumYear { get => _timelineMinimumYear; private set => SetProperty(ref _timelineMinimumYear, value); }
    public int TimelineMaximumYear { get => _timelineMaximumYear; private set => SetProperty(ref _timelineMaximumYear, value); }
    public double TimelineYear
    {
        get => _timelineYear;
        set
        {
            if (!SetProperty(ref _timelineYear, value)) return;
            RaisePropertyChanged(nameof(TimelineYearText));
            FilterTimelineEvents();
        }
    }
    public string TimelineYearText => TimelineExplorerEvents.Count == 0
        ? "Choose a show"
        : $"{TimelineExplorerEvents.Count:N0} events · {TimelineMinimumYear}–{TimelineMaximumYear}";
    public bool HasTimelineExplorerEvents => FilteredTimelineEvents.Count > 0;
    public bool HasSearchSuggestions => SearchSuggestions.Count > 0;

    public WikiCitationRecord? SelectedCitation
    {
        get => _selectedCitation;
        set
        {
            if (!SetProperty(ref _selectedCitation, value)) return;
            if (value is not null) LoadCitationEditor(value);
            RaiseCommandState();
        }
    }
    public string CitationSourceTitle { get => _citationSourceTitle; set { if (SetProperty(ref _citationSourceTitle, value)) RaiseCommandState(); } }
    public string CitationSourceType { get => _citationSourceType; set => SetProperty(ref _citationSourceType, value); }
    public string CitationPublisher { get => _citationPublisher; set => SetProperty(ref _citationPublisher, value); }
    public string CitationAuthor { get => _citationAuthor; set => SetProperty(ref _citationAuthor, value); }
    public string CitationUrl { get => _citationUrl; set => SetProperty(ref _citationUrl, value); }
    public string CitationPublishedDate { get => _citationPublishedDate; set => SetProperty(ref _citationPublishedDate, value); }
    public string CitationLocator { get => _citationLocator; set => SetProperty(ref _citationLocator, value); }
    public string CitationQuotedText { get => _citationQuotedText; set => SetProperty(ref _citationQuotedText, value); }
    public string CitationNote { get => _citationNote; set => SetProperty(ref _citationNote, value); }

    public WikiImageDraft? SelectedImage
    {
        get => _selectedImage;
        set
        {
            if (!SetProperty(ref _selectedImage, value)) return;
            if (value is not null) LoadImageEditor(value);
            RaiseCommandState();
        }
    }
    public string ImageCaption { get => _imageCaption; set => SetProperty(ref _imageCaption, value); }
    public string ImageAltText { get => _imageAltText; set => SetProperty(ref _imageAltText, value); }
    public string ImageCreator { get => _imageCreator; set => SetProperty(ref _imageCreator, value); }
    public string ImageCopyright { get => _imageCopyright; set => SetProperty(ref _imageCopyright, value); }
    public string ImageLicence { get => _imageLicence; set => SetProperty(ref _imageLicence, value); }
    public string ImageCapturedDate { get => _imageCapturedDate; set => SetProperty(ref _imageCapturedDate, value); }
    public string ImageRepresentativeFrom { get => _imageRepresentativeFrom; set => SetProperty(ref _imageRepresentativeFrom, value); }
    public string ImageRepresentativeTo { get => _imageRepresentativeTo; set => SetProperty(ref _imageRepresentativeTo, value); }
    public string ImageDateNotes { get => _imageDateNotes; set => SetProperty(ref _imageDateNotes, value); }
    public string ImageRole { get => _imageRole; set => SetProperty(ref _imageRole, value); }
    public string ImageFileName { get => _imageFileName; private set { if (SetProperty(ref _imageFileName, value)) RaiseCommandState(); } }

    public WikiTimelineEventRecord? SelectedTimelineEvent
    {
        get => _selectedTimelineEvent;
        set
        {
            if (!SetProperty(ref _selectedTimelineEvent, value)) return;
            if (value is not null) LoadTimelineEditor(value);
            RaiseCommandState();
        }
    }
    public string TimelineTitle { get => _timelineTitle; set { if (SetProperty(ref _timelineTitle, value)) RaiseCommandState(); } }
    public string TimelineSummary { get => _timelineSummary; set => SetProperty(ref _timelineSummary, value); }
    public string TimelineCategory { get => _timelineCategory; set => SetProperty(ref _timelineCategory, value); }
    public string TimelineStartDate { get => _timelineStartDate; set => SetProperty(ref _timelineStartDate, value); }
    public string TimelineEndDate { get => _timelineEndDate; set => SetProperty(ref _timelineEndDate, value); }
    public string TimelineDateDisplay { get => _timelineDateDisplay; set => SetProperty(ref _timelineDateDisplay, value); }
    public string TimelineSignificance { get => _timelineSignificance; set => SetProperty(ref _timelineSignificance, value); }
    public string ArchiveSearchText { get => _archiveSearchText; set => SetProperty(ref _archiveSearchText, value); }
    public WikiArchiveBroadcastCandidate? SelectedArchiveBroadcast { get => _selectedArchiveBroadcast; set { if (SetProperty(ref _selectedArchiveBroadcast, value)) RaiseCommandState(); } }
    public WikiArchiveMomentCandidate? SelectedArchiveMoment { get => _selectedArchiveMoment; set { if (SetProperty(ref _selectedArchiveMoment, value)) RaiseCommandState(); } }
    public WikiTimelineBroadcastLink? SelectedTimelineLink { get => _selectedTimelineLink; set => SetProperty(ref _selectedTimelineLink, value); }
    public string TimelineLinksText => TimelineLinks.Count == 0 ? "No broadcasts linked yet." : $"{TimelineLinks.Count:N0} archive link{(TimelineLinks.Count == 1 ? string.Empty : "s")} staged.";

    public async Task LoadAsync(bool force = false)
    {
        if (_isLoaded && !force) return;
        IsBusy = true;
        StatusText = "Loading the wiki…";
        try
        {
            var automatic = await _wiki.RunAutomaticTopicCleanupAsync().ConfigureAwait(true);
            Overview = await _wiki.GetOverviewAsync().ConfigureAwait(true);
            StarterPreview = await _wiki.PreviewStarterPagesAsync().ConfigureAwait(true);
            await LoadDashboardCollectionsAsync().ConfigureAwait(true);
            await LoadPagesCoreAsync().ConfigureAwait(true);
            _isLoaded = true;
            StatusText = automatic.GroupsMerged > 0 ? automatic.Summary : OverviewText;
        }
        finally { IsBusy = false; }
    }

    private async Task LoadPagesAsync()
    {
        IsBusy = true;
        try
        {
            EnterMode(WikiNavigationMode.Browse, true);
            await LoadPagesCoreAsync().ConfigureAwait(true);
        }
        finally { IsBusy = false; }
    }

    private async Task LoadDashboardCollectionsAsync()
    {
        var all = await _wiki.BrowseAsync(new WikiBrowseQuery(Limit: 5000)).ConfigureAwait(true);
        _allPages.Clear();
        _allPages.AddRange(all);
        ReplaceInlineLinkTargets(_document);
        UpdateSearchSuggestions();
        ReplaceDashboardCollection(FeaturedPages, all
            .OrderByDescending(x => string.Equals(x.Status, "Published", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.CitationCount + x.ImageCount + x.TimelineEventCount)
            .ThenByDescending(x => x.UpdatedAt).Take(6));
        ReplaceDashboardCollection(ShowPages, all.Where(x => x.PageType == "Show").OrderByDescending(x => x.TimelineEventCount).ThenBy(x => x.Title).Take(10));
        ReplaceDashboardCollection(PeoplePages, all.Where(x => x.PageType == "Person").OrderByDescending(x => x.CitationCount).ThenBy(x => x.Title).Take(10));
        ReplaceDashboardCollection(TopicPages, all.Where(x => x.PageType == "Topic").OrderByDescending(x => x.CitationCount).ThenBy(x => x.Title).Take(10));
        ReplaceDashboardCollection(RecentPages, all.OrderByDescending(x => x.UpdatedAt).Take(8));
        ReplaceDashboardCollection(TimelinePages, all.Where(x => x.TimelineEventCount > 0).OrderByDescending(x => x.TimelineEventCount).ThenBy(x => x.Title).Take(8));
        var today = DateTime.Today;
        var highlights = await _wiki.GetDashboardHighlightsAsync(today.Month, today.Day).ConfigureAwait(true);
        OnThisDayItems.Clear();
        foreach (var item in highlights.OnThisDay) OnThisDayItems.Add(item);
        Eras.Clear();
        foreach (var era in highlights.Eras) Eras.Add(era);
        TimelineShows.Clear();
        foreach (var show in await _wiki.GetTimelineShowsAsync().ConfigureAwait(true)) TimelineShows.Add(show);
        if (SelectedTimelineShow is null && TimelineShows.Count > 0) SelectedTimelineShow = TimelineShows[0];
        RaisePropertyChanged(nameof(HasFeaturedPages));
        RaisePropertyChanged(nameof(HasShowPages));
        RaisePropertyChanged(nameof(HasPeoplePages));
        RaisePropertyChanged(nameof(HasTopicPages));
        RaisePropertyChanged(nameof(HasRecentPages));
        RaisePropertyChanged(nameof(HasTimelinePages));
        RaisePropertyChanged(nameof(HasOnThisDay));
        RaisePropertyChanged(nameof(HasEras));
    }

    private static void ReplaceDashboardCollection(ObservableCollection<WikiPageSummary> target, IEnumerable<WikiPageSummary> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private async Task LoadPagesCoreAsync(Guid? preferredPageId = null)
    {
        var type = SelectedTypeFilter == "All types" ? string.Empty : SelectedTypeFilter;
        var status = SelectedStatusFilter == "All statuses" ? string.Empty : SelectedStatusFilter;
        var values = await _wiki.BrowseAsync(new WikiBrowseQuery(SearchText, type, status)).ConfigureAwait(true);
        Pages.Clear();
        foreach (var item in values) Pages.Add(item);
        RaisePropertyChanged(nameof(HasPages));
        RaisePropertyChanged(nameof(HasNoPages));
        if (preferredPageId.HasValue && Pages.FirstOrDefault(x => x.PageId == preferredPageId.Value) is { } preferred)
            await OpenPageAsync(preferred, false).ConfigureAwait(true);
        StatusText = $"Showing {Pages.Count:N0} wiki pages.";
    }

    private async Task LoadPageAsync(Guid pageId, int version)
    {
        try
        {
            IsBusy = true;
            var page = await _wiki.GetPageAsync(pageId).ConfigureAwait(true);
            if (version != _selectionVersion || page is null) return;
            ApplyDocument(page);
            await LoadReaderAssetsAsync(page, version).ConfigureAwait(true);
            var navigation = await _wiki.GetNavigationContextAsync(page.PageId).ConfigureAwait(true);
            if (version != _selectionVersion) return;
            RelatedPages.Clear();
            foreach (var item in navigation.RelatedPages) RelatedPages.Add(item);
            BacklinkPages.Clear();
            foreach (var item in navigation.Backlinks) BacklinkPages.Add(item);
            MissingLinks.Clear();
            foreach (var item in navigation.MissingLinks) MissingLinks.Add(item);
            RaisePropertyChanged(nameof(HasRelatedPages));
            RaisePropertyChanged(nameof(HasBacklinks));
            RaisePropertyChanged(nameof(HasMissingLinks));
            StatusText = $"Loaded {page.Title}, revision {page.Revision:N0}.";
        }
        catch (Exception exception) { SetError(exception); }
        finally { IsBusy = false; }
    }

    private Task OpenPageAsync(WikiPageSummary page, bool recordHistory)
    {
        if (recordHistory) PushCurrentToBackHistory();
        _selectedPage = page;
        RaisePropertyChanged(nameof(SelectedPage));
        IsDashboardMode = false;
        IsBrowseMode = false;
        IsTimelineExplorerMode = false;
        IsReadingMode = true;
        var version = ++_selectionVersion;
        _ = LoadPageAsync(page.PageId, version);
        return Task.CompletedTask;
    }

    private Task NewPageAsync()
    {
        PushCurrentToBackHistory();
        _selectionVersion++;
        _selectedPage = null;
        RaisePropertyChanged(nameof(SelectedPage));
        _document = null;
        _editorPageId = null;
        _editorRevision = 0;
        EditorTitle = "Untitled wiki page";
        EditorSlug = string.Empty;
        EditorPageType = "Show";
        EditorStatus = "Draft";
        EditorSummary = string.Empty;
        EditorBodyMarkdown = "# Untitled wiki page\n\nStart writing here. Add source citations through an exported authoring pack.";
        EditorAliases = string.Empty;
        EditorChangeSummary = "Created page";
        IsDashboardMode = false;
        IsBrowseMode = false;
        IsTimelineExplorerMode = false;
        IsReadingMode = false;
        ReplaceEvidence(null);
        ReaderImages.Clear();
        Revisions.Clear();
        RaisePropertyChanged(nameof(HasReaderImages));
        RaisePropertyChanged(nameof(HasRevisions));
        RaiseEditorState();
        StatusText = "New draft ready. Give it a title and save it.";
        return Task.CompletedTask;
    }

    private async Task SaveAsync()
    {
        IsBusy = true;
        StatusText = "Saving the wiki page…";
        try
        {
            var result = await _wiki.SavePageAsync(new WikiPageDraft(
                _editorPageId,
                EditorSlug,
                EditorTitle,
                EditorPageType,
                EditorSummary,
                EditorBodyMarkdown,
                EditorStatus,
                _editorRevision,
                string.IsNullOrWhiteSpace(EditorChangeSummary) ? "Manual edit" : EditorChangeSummary,
                Environment.UserName,
                EditorAliases.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                Citations.ToArray(), ImageDrafts.ToArray(), Timeline.ToArray()))
                .ConfigureAwait(true);
            Overview = await _wiki.GetOverviewAsync().ConfigureAwait(true);
            await LoadDashboardCollectionsAsync().ConfigureAwait(true);
            await LoadPagesCoreAsync(result.PageId).ConfigureAwait(true);
            StatusText = result.Created ? "Explore page created." : $"Explore page saved as revision {result.Revision:N0}.";
        }
        finally { IsBusy = false; }
    }

    private Task RevertAsync()
    {
        if (_document is not null) ApplyDocument(_document);
        StatusText = "Unsaved changes reverted.";
        return Task.CompletedTask;
    }

    private async Task ClearFiltersAsync()
    {
        SearchText = string.Empty;
        SelectedTypeFilter = "All types";
        SelectedStatusFilter = "All statuses";
        await LoadPagesCoreAsync().ConfigureAwait(true);
    }

    private async Task ChooseImportAsync()
    {
        var path = await _files.PickOpenFileAsync(new FileSelectionRequest(
            Title: "Choose a Radio Vault wiki authoring pack",
            Filter: "Radio Vault wiki packs|*.rvwiki;*.zip|All files|*.*")).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;
        IsBusy = true;
        StatusText = "Checking wiki pages, citations, images and timeline events…";
        await Task.Yield();
        try
        {
            ImportPreview = await _packs.PreviewImportAsync(path).ConfigureAwait(true);
            StatusText = ImportPreview.Summary;
        }
        finally { IsBusy = false; }
    }

    private async Task ApplyImportAsync()
    {
        if (!HasPendingImport) return;
        IsBusy = true;
        StatusText = "Importing the reviewed wiki pack…";
        try
        {
            var result = await _packs.ApplyImportAsync().ConfigureAwait(true);
            ImportPreview = null;
            Overview = await _wiki.GetOverviewAsync().ConfigureAwait(true);
            await LoadDashboardCollectionsAsync().ConfigureAwait(true);
            await LoadPagesCoreAsync().ConfigureAwait(true);
            await AuditCitationsAsync().ConfigureAwait(true);
            StatusText = $"{result.Summary} {CitationAuditSummary}";
        }
        finally { IsBusy = false; }
    }

    private async Task CancelImportAsync()
    {
        await _packs.CancelImportAsync().ConfigureAwait(true);
        ImportPreview = null;
        StatusText = "Explore import cancelled. Nothing was changed.";
    }

    private async Task ExportAsync()
    {
        IsBusy = true;
        StatusText = "Building the agent-friendly wiki authoring pack…";
        try
        {
            var export = await _packs.ExportAsync().ConfigureAwait(true);
            var path = await _files.PickSaveFileAsync(new FileSelectionRequest(
                Title: "Export Radio Vault wiki authoring pack",
                Filter: "Radio Vault wiki packs|*.rvwiki",
                DefaultExtension: ".rvwiki",
                SuggestedFileName: export.FileName,
                CheckFileExists: false)).ConfigureAwait(true);
            if (string.IsNullOrWhiteSpace(path))
            {
                StatusText = "Explore export cancelled.";
                return;
            }
            await File.WriteAllBytesAsync(path, export.Bytes).ConfigureAwait(true);
            StatusText = $"Exported {export.PageCount:N0} pages and {export.ImageCount:N0} images to {Path.GetFileName(path)}.";
        }
        finally { IsBusy = false; }
    }

    private void ApplyDocument(WikiPageDocument page)
    {
        _document = page;
        ReplaceInlineLinkTargets(page);
        _editorPageId = page.PageId;
        _editorRevision = page.Revision;
        EditorTitle = page.Title;
        EditorSlug = page.Slug;
        EditorPageType = page.PageType;
        EditorStatus = page.Status;
        EditorSummary = page.Summary;
        EditorBodyMarkdown = page.BodyMarkdown;
        EditorAliases = string.Join(", ", page.Aliases);
        EditorChangeSummary = string.Empty;
        ReplaceEvidence(page);
        IsReadingMode = true;
        RaiseReaderState();
        RaiseEditorState();
    }

    private void ReplaceInlineLinkTargets(WikiPageDocument? page)
    {
        InlineLinkTargets.Clear();
        var targets = (page?.EntityLinks ?? [])
            .Where(value => value.Relationship.StartsWith("inline", StringComparison.OrdinalIgnoreCase))
            .Select(value => value.Label)
            .Concat(_allPages
                .Where(value => page is null || value.PageId != page.PageId)
                .Select(value => value.Title))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(value => value.Length);
        foreach (var title in targets)
            InlineLinkTargets.Add(title);
    }

    private void ReplaceEvidence(WikiPageDocument? page)
    {
        Citations.Clear();
        Images.Clear();
        ImageDrafts.Clear();
        Timeline.Clear();
        if (page is not null)
        {
            foreach (var value in page.Citations.OrderBy(x => x.Ordinal)) Citations.Add(value);
            foreach (var value in page.Images.OrderBy(x => x.SortOrder))
            {
                Images.Add(value);
                ImageDrafts.Add(new WikiImageDraft(value));
            }
            foreach (var value in page.Timeline.OrderBy(x => x.StartDate).ThenBy(x => x.SortOrder)) Timeline.Add(value);
        }
        RaisePropertyChanged(nameof(HasCitations));
        RaisePropertyChanged(nameof(HasImages));
        RaisePropertyChanged(nameof(HasTimeline));
        RaisePropertyChanged(nameof(ReaderEvidence));
        RaisePropertyChanged(nameof(ReaderTimelineRange));
    }

    private async Task LoadReaderAssetsAsync(WikiPageDocument page, int version)
    {
        var readerImages = new List<WikiReaderImageViewModel>();
        foreach (var link in page.Images.OrderBy(x => x.SortOrder))
        {
            var content = await _wiki.GetImageAsync(link.ImageId).ConfigureAwait(true);
            if (version != _selectionVersion) return;
            if (content is not null) readerImages.Add(new WikiReaderImageViewModel(link, content.Content));
        }
        var revisions = await _wiki.GetRevisionsAsync(page.PageId).ConfigureAwait(true);
        if (version != _selectionVersion) return;
        ReaderImages.Clear();
        foreach (var image in readerImages) ReaderImages.Add(image);
        Revisions.Clear();
        foreach (var revision in revisions) Revisions.Add(revision);
        SelectedRevision = Revisions.FirstOrDefault();
        RaisePropertyChanged(nameof(HasReaderImages));
        RaisePropertyChanged(nameof(HasRevisions));
    }

    private async Task GenerateStartersAsync()
    {
        IsBusy = true;
        StatusText = "Generating starter pages from shows, people and recurring topics…";
        try
        {
            var result = await _wiki.GenerateStarterPagesAsync().ConfigureAwait(true);
            Overview = await _wiki.GetOverviewAsync().ConfigureAwait(true);
            StarterPreview = await _wiki.PreviewStarterPagesAsync().ConfigureAwait(true);
            await LoadDashboardCollectionsAsync().ConfigureAwait(true);
            await LoadPagesCoreAsync(result.CreatedPageIds.FirstOrDefault()).ConfigureAwait(true);
            StatusText = result.Summary;
        }
        finally { IsBusy = false; }
    }

    private Task NewCitationAsync()
    {
        _selectedCitation = null;
        RaisePropertyChanged(nameof(SelectedCitation));
        CitationSourceTitle = string.Empty;
        CitationSourceType = "Web";
        CitationPublisher = string.Empty;
        CitationAuthor = string.Empty;
        CitationUrl = string.Empty;
        CitationPublishedDate = string.Empty;
        CitationLocator = string.Empty;
        CitationQuotedText = string.Empty;
        CitationNote = string.Empty;
        return Task.CompletedTask;
    }

    private Task SaveCitationAsync()
    {
        if (_editorPageId is not { } pageId) return Task.CompletedTask;
        var sourceId = SelectedCitation?.SourceId ?? Guid.NewGuid();
        var source = new WikiSourceRecord(sourceId, CitationSourceType, CitationSourceTitle, CitationAuthor, CitationPublisher,
            CitationUrl, string.Empty, ParseDate(CitationPublishedDate), DatePrecision(CitationPublishedDate), DateTimeOffset.UtcNow,
            null, string.Empty, null, null, null, null, CitationLocator, string.Empty);
        var citation = new WikiCitationRecord(SelectedCitation?.CitationId ?? Guid.NewGuid(), pageId, sourceId,
            SelectedCitation?.Ordinal ?? (Citations.Count + 1), string.Empty, CitationQuotedText, CitationNote, source);
        ReplaceOrAdd(Citations, SelectedCitation, citation);
        SelectedCitation = citation;
        EditorChangeSummary = "Updated citations and sources";
        RaisePropertyChanged(nameof(HasCitations));
        StatusText = "Citation staged. Save the page to commit it.";
        return Task.CompletedTask;
    }

    private Task RemoveCitationAsync()
    {
        if (SelectedCitation is not null) Citations.Remove(SelectedCitation);
        _selectedCitation = null;
        RaisePropertyChanged(nameof(SelectedCitation));
        RaisePropertyChanged(nameof(HasCitations));
        EditorChangeSummary = "Updated citations and sources";
        StatusText = "Citation removal staged. Save the page to commit it.";
        return Task.CompletedTask;
    }

    private void LoadCitationEditor(WikiCitationRecord value)
    {
        CitationSourceTitle = value.Source?.Title ?? string.Empty;
        CitationSourceType = value.Source?.SourceType ?? "Web";
        CitationPublisher = value.Source?.Publisher ?? string.Empty;
        CitationAuthor = value.Source?.Author ?? string.Empty;
        CitationUrl = value.Source?.Url ?? string.Empty;
        CitationPublishedDate = value.Source?.PublishedDate?.ToString("yyyy-MM-dd") ?? string.Empty;
        CitationLocator = value.Source?.Locator ?? string.Empty;
        CitationQuotedText = value.QuotedText;
        CitationNote = value.Note;
    }

    private Task NewImageAsync()
    {
        _selectedImage = null;
        RaisePropertyChanged(nameof(SelectedImage));
        _pendingImageBytes = null;
        ImageFileName = string.Empty;
        ImageCaption = ImageAltText = ImageCreator = ImageCopyright = ImageLicence = string.Empty;
        ImageCapturedDate = ImageRepresentativeFrom = ImageRepresentativeTo = ImageDateNotes = string.Empty;
        ImageRole = "Article";
        return Task.CompletedTask;
    }

    private async Task ChooseImageAsync()
    {
        var path = await _files.PickOpenFileAsync(new FileSelectionRequest(
            Title: "Choose a sourced Explore image",
            Filter: "Images|*.png;*.jpg;*.jpeg;*.webp;*.gif;*.avif|All files|*.*")).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(path)) return;
        var file = new FileInfo(path);
        if (file.Length > 25 * 1024 * 1024) throw new InvalidDataException("Explore images are limited to 25 MB each.");
        _pendingImageBytes = await File.ReadAllBytesAsync(path).ConfigureAwait(true);
        ImageFileName = file.Name;
        if (string.IsNullOrWhiteSpace(ImageCaption)) ImageCaption = Path.GetFileNameWithoutExtension(file.Name);
        StatusText = "Image loaded. Add its date, creator and licence, then stage it.";
    }

    private Task SaveImageAsync()
    {
        if (_editorPageId is not { } pageId) return Task.CompletedTask;
        var current = SelectedImage?.Link.Image;
        var id = current?.ImageId ?? Guid.NewGuid();
        var bytes = _pendingImageBytes ?? SelectedImage?.Content;
        var fileName = string.IsNullOrWhiteSpace(ImageFileName) ? current?.OriginalFileName ?? "wiki-image" : ImageFileName;
        var contentLength = bytes?.LongLength ?? current?.ByteCount ?? 0;
        var hash = bytes is null ? current?.Sha256 ?? string.Empty : WikiAuthoringPackService.Sha256(bytes);
        var image = new WikiImageRecord(id, fileName, MediaType(fileName), hash, contentLength, ImageCaption, ImageAltText,
            ImageCreator, ImageCopyright, ImageLicence, current?.SourceId, ParseDate(ImageCapturedDate),
            ParseDate(ImageRepresentativeFrom), ParseDate(ImageRepresentativeTo), DatePrecision(ImageCapturedDate), ImageDateNotes);
        var draft = new WikiImageDraft(new WikiPageImageLink(pageId, id, ImageRole, SelectedImage?.Link.SortOrder ?? ImageDrafts.Count, image), bytes);
        ReplaceOrAdd(ImageDrafts, SelectedImage, draft);
        Images.Clear();
        foreach (var value in ImageDrafts) Images.Add(value.Link);
        SelectedImage = draft;
        _pendingImageBytes = null;
        EditorChangeSummary = "Updated dated images";
        RaisePropertyChanged(nameof(HasImages));
        StatusText = "Image staged. Save the page to commit it.";
        return Task.CompletedTask;
    }

    private Task RemoveImageAsync()
    {
        if (SelectedImage is not null) ImageDrafts.Remove(SelectedImage);
        Images.Clear();
        foreach (var value in ImageDrafts) Images.Add(value.Link);
        _selectedImage = null;
        RaisePropertyChanged(nameof(SelectedImage));
        RaisePropertyChanged(nameof(HasImages));
        EditorChangeSummary = "Updated dated images";
        return Task.CompletedTask;
    }

    private void LoadImageEditor(WikiImageDraft value)
    {
        var image = value.Link.Image;
        if (image is null) return;
        ImageFileName = image.OriginalFileName;
        ImageCaption = image.Caption;
        ImageAltText = image.AltText;
        ImageCreator = image.Creator;
        ImageCopyright = image.CopyrightHolder;
        ImageLicence = image.Licence;
        ImageCapturedDate = image.CapturedDate?.ToString("yyyy-MM-dd") ?? string.Empty;
        ImageRepresentativeFrom = image.RepresentativeFrom?.ToString("yyyy-MM-dd") ?? string.Empty;
        ImageRepresentativeTo = image.RepresentativeTo?.ToString("yyyy-MM-dd") ?? string.Empty;
        ImageDateNotes = image.DateNotes;
        ImageRole = value.Link.Role;
        _pendingImageBytes = null;
    }

    private Task NewTimelineAsync()
    {
        _selectedTimelineEvent = null;
        RaisePropertyChanged(nameof(SelectedTimelineEvent));
        TimelineTitle = TimelineSummary = TimelineStartDate = TimelineEndDate = TimelineDateDisplay = string.Empty;
        TimelineCategory = "Milestone";
        TimelineSignificance = "50";
        TimelineLinks.Clear();
        RaisePropertyChanged(nameof(TimelineLinksText));
        return Task.CompletedTask;
    }

    private Task SaveTimelineAsync()
    {
        if (_editorPageId is not { } pageId) return Task.CompletedTask;
        _ = int.TryParse(TimelineSignificance, out var significance);
        var start = ParseDate(TimelineStartDate);
        var display = string.IsNullOrWhiteSpace(TimelineDateDisplay)
            ? start?.ToString("dd MMMM yyyy") ?? "Date unknown"
            : TimelineDateDisplay.Trim();
        var eventId = SelectedTimelineEvent?.EventId ?? Guid.NewGuid();
        var value = new WikiTimelineEventRecord(eventId, pageId, TimelineTitle, TimelineSummary, TimelineCategory,
            start, ParseDate(TimelineEndDate), DatePrecision(TimelineStartDate), display, Math.Clamp(significance, 0, 100),
            SelectedTimelineEvent?.SortOrder ?? Timeline.Count, SelectedTimelineEvent?.SourceIds ?? Array.Empty<Guid>(),
            SelectedTimelineEvent?.ImageIds ?? Array.Empty<Guid>(), TimelineLinks.Select(x => x with { EventId = eventId }).ToArray());
        ReplaceOrAdd(Timeline, SelectedTimelineEvent, value);
        SelectedTimelineEvent = value;
        EditorChangeSummary = "Updated the show timeline";
        RaisePropertyChanged(nameof(HasTimeline));
        StatusText = "Timeline event staged. Save the page to commit it.";
        return Task.CompletedTask;
    }

    private Task RemoveTimelineAsync()
    {
        if (SelectedTimelineEvent is not null) Timeline.Remove(SelectedTimelineEvent);
        _selectedTimelineEvent = null;
        RaisePropertyChanged(nameof(SelectedTimelineEvent));
        RaisePropertyChanged(nameof(HasTimeline));
        EditorChangeSummary = "Updated the show timeline";
        return Task.CompletedTask;
    }

    private void LoadTimelineEditor(WikiTimelineEventRecord value)
    {
        TimelineTitle = value.Title;
        TimelineSummary = value.Summary;
        TimelineCategory = value.Category;
        TimelineStartDate = value.StartDate?.ToString("yyyy-MM-dd") ?? string.Empty;
        TimelineEndDate = value.EndDate?.ToString("yyyy-MM-dd") ?? string.Empty;
        TimelineDateDisplay = value.DateDisplay;
        TimelineSignificance = value.Significance.ToString();
        TimelineLinks.Clear();
        foreach (var link in value.Broadcasts) TimelineLinks.Add(link);
        RaisePropertyChanged(nameof(TimelineLinksText));
    }

    private async Task SearchArchiveAsync()
    {
        var result = await _wiki.BrowseArchiveLinksAsync(new WikiArchiveBrowseQuery(ArchiveSearchText)).ConfigureAwait(true);
        ArchiveBroadcasts.Clear();
        ArchiveMoments.Clear();
        foreach (var value in result.Broadcasts) ArchiveBroadcasts.Add(value);
        foreach (var value in result.Moments) ArchiveMoments.Add(value);
        StatusText = $"Found {ArchiveBroadcasts.Count:N0} broadcasts and {ArchiveMoments.Count:N0} Moments that can be linked.";
    }

    private Task AddBroadcastLinkAsync()
    {
        if (SelectedArchiveBroadcast is not { } broadcast) return Task.CompletedTask;
        if (TimelineLinks.All(x => x.EpisodeId != broadcast.EpisodeId || x.MomentId is not null))
            TimelineLinks.Add(new WikiTimelineBroadcastLink(Guid.Empty, broadcast.EpisodeId, null, null, null, broadcast.DisplayText, TimelineLinks.Count));
        RaisePropertyChanged(nameof(TimelineLinksText));
        return Task.CompletedTask;
    }

    private Task AddMomentLinkAsync()
    {
        if (SelectedArchiveMoment is not { } moment) return Task.CompletedTask;
        if (TimelineLinks.All(x => x.MomentId != moment.MomentId))
            TimelineLinks.Add(new WikiTimelineBroadcastLink(Guid.Empty, moment.EpisodeId, moment.MomentId,
                moment.PositionMs, null, moment.DisplayText, TimelineLinks.Count));
        RaisePropertyChanged(nameof(TimelineLinksText));
        return Task.CompletedTask;
    }

    private Task RemoveTimelineLinkAsync()
    {
        if (SelectedTimelineLink is not null) TimelineLinks.Remove(SelectedTimelineLink);
        SelectedTimelineLink = null;
        RaisePropertyChanged(nameof(TimelineLinksText));
        return Task.CompletedTask;
    }

    private static void ReplaceOrAdd<T>(ObservableCollection<T> values, T? oldValue, T newValue) where T : class
    {
        if (oldValue is not null)
        {
            var index = values.IndexOf(oldValue);
            if (index >= 0) { values[index] = newValue; return; }
        }
        values.Add(newValue);
    }

    private static DateOnly? ParseDate(string? value)
        => DateOnly.TryParse(value, out var parsed) ? parsed : null;

    private static string DatePrecision(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length switch { 4 => "Year", 7 => "Month", >= 10 => "Day", _ => "Unknown" };
    }

    private static string MediaType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".avif" => "image/avif",
        _ => "application/octet-stream"
    };

    private void RaiseEditorState()
    {
        RaisePropertyChanged(nameof(HasSelection));
        RaisePropertyChanged(nameof(HasExistingPage));
        RaisePropertyChanged(nameof(CanSave));
        RaisePropertyChanged(nameof(EditorRevisionText));
        RaisePropertyChanged(nameof(SelectionHeading));
        RaiseCommandState();
    }

    private Task ShowReadingModeAsync()
    {
        IsReadingMode = true;
        return Task.CompletedTask;
    }

    private Task ShowEditingModeAsync()
    {
        IsReadingMode = false;
        return Task.CompletedTask;
    }

    private Task ShowDashboardAsync()
    {
        EnterMode(WikiNavigationMode.Dashboard, true);
        IsReadingMode = true;
        StatusText = "Explore dashboard ready.";
        return Task.CompletedTask;
    }

    private async Task BrowseWikiAsync()
    {
        SearchText = string.Empty;
        SelectedTypeFilter = "All types";
        SelectedStatusFilter = "All statuses";
        EnterMode(WikiNavigationMode.Browse, true);
        IsReadingMode = true;
        await LoadPagesCoreAsync().ConfigureAwait(true);
    }

    private async Task ManageWikiAsync()
    {
        if (Pages.Count == 0) await LoadPagesCoreAsync().ConfigureAwait(true);
        if (SelectedPage is null && Pages.Count > 0) await OpenPageAsync(Pages[0], true).ConfigureAwait(true);
        else if (SelectedPage is not null && !IsArticleMode) await OpenPageAsync(SelectedPage, true).ConfigureAwait(true);
        IsReadingMode = false;
        StatusText = "Explore management workspace ready.";
    }

    private Task OpenRandomPageAsync()
    {
        var candidates = FeaturedPages.Concat(ShowPages).Concat(PeoplePages).Concat(TopicPages)
            .GroupBy(x => x.PageId).Select(x => x.First()).ToArray();
        if (candidates.Length > 0) _ = OpenPageAsync(candidates[Random.Shared.Next(candidates.Length)], true);
        return Task.CompletedTask;
    }

    private Task ShowTimelineExplorerAsync()
    {
        EnterMode(WikiNavigationMode.Timeline, true);
        IsReadingMode = true;
        if (SelectedTimelineShow is null && TimelineShows.Count > 0) SelectedTimelineShow = TimelineShows[0];
        StatusText = "Timeline Explorer ready.";
        return Task.CompletedTask;
    }

    private async Task OpenEraAsync(WikiEraSummary era)
    {
        await ShowTimelineExplorerAsync().ConfigureAwait(true);
        TimelineYear = Math.Clamp(era.StartYear + 4, TimelineMinimumYear, TimelineMaximumYear);
    }

    private async Task OpenInternalWikiLinkAsync(string? target)
    {
        try
        {
            var normalized = (target ?? string.Empty).Trim().Trim('[', ']').Trim();
            if (normalized.StartsWith("wiki:", StringComparison.OrdinalIgnoreCase)) normalized = normalized[5..];
            normalized = Uri.UnescapeDataString(normalized).Replace('-', ' ');
            if (normalized.Length == 0) return;
            var typedLink = (_document?.EntityLinks ?? []).FirstOrDefault(value =>
                value.Relationship.StartsWith("inline", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(value.Label.Trim(), normalized, StringComparison.CurrentCultureIgnoreCase));
            if (typedLink is not null && await TryOpenEntityLinkAsync(typedLink).ConfigureAwait(true)) return;
            var candidates = await _wiki.BrowseAsync(new WikiBrowseQuery(normalized, Limit: 50)).ConfigureAwait(true);
            var page = candidates.FirstOrDefault(x => string.Equals(x.Slug, target?.Replace("wiki:", string.Empty, StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(x => string.Equals(x.Title, normalized, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault();
            if (page is null)
            {
                SearchText = normalized;
                EnterMode(WikiNavigationMode.Browse, true);
                await LoadPagesCoreAsync().ConfigureAwait(true);
                StatusText = $"No exact Explore page matches '{normalized}'. Showing related results.";
            }
            else await OpenPageAsync(page, true).ConfigureAwait(true);
        }
        catch (Exception exception) { SetError(exception); }
    }

    private async Task<bool> TryOpenEntityLinkAsync(ArchiveEntityLink link)
    {
        var destination = ArchiveEntityNavigation.Resolve(link);
        if (destination.Destination == ArchiveEntityDestination.Explore &&
            Guid.TryParse(destination.TargetId, out var pageId) &&
            _allPages.FirstOrDefault(value => value.PageId == pageId) is { } page)
        {
            await OpenPageAsync(page, true).ConfigureAwait(true);
            return true;
        }
        if (destination.Destination == ArchiveEntityDestination.Broadcast &&
            long.TryParse(destination.TargetId, out var episodeId) &&
            _openBroadcastInfo is not null)
        {
            await _openBroadcastInfo(episodeId).ConfigureAwait(true);
            return true;
        }
        if (_openEntityLink is null) return false;
        await _openEntityLink(link).ConfigureAwait(true);
        return true;
    }

    public Task OpenEntityAsync(string entity)
        => OpenInternalWikiLinkAsync(entity);

    private async Task PlayTimelineLinkAsync(WikiTimelineBroadcastLink? link)
    {
        if (link is null) return;
        if (_openBroadcastInfo is null)
        {
            StatusText = "Broadcast information is unavailable in this Explore session.";
            return;
        }
        try
        {
            await _openBroadcastInfo(link.EpisodeId).ConfigureAwait(true);
            StatusText = "Opened the linked broadcast information.";
        }
        catch (Exception exception) { SetError(exception); }
    }

    private async Task OpenCitationArchiveLinkAsync(WikiCitationRecord? citation)
    {
        var source = citation?.Source;
        if (source?.EpisodeId is not > 0) return;
        if (_openBroadcastInfo is null)
        {
            StatusText = "Broadcast information is unavailable in this Explore session.";
            return;
        }
        try
        {
            await _openBroadcastInfo(source.EpisodeId.Value).ConfigureAwait(true);
            StatusText = "Opened the linked broadcast information.";
        }
        catch (Exception exception) { SetError(exception); }
    }

    private async Task RestoreSelectedRevisionAsync()
    {
        if (SelectedRevision is not { } revision || _document is null) return;
        IsBusy = true;
        try
        {
            var result = await _wiki.RestoreRevisionAsync(new WikiRevisionRestoreRequest(
                _document.PageId, revision.Revision, _document.Revision, Environment.UserName)).ConfigureAwait(true);
            await LoadPagesCoreAsync(result.PageId).ConfigureAwait(true);
            StatusText = $"Revision {revision.Revision:N0} was restored safely as new revision {result.Revision:N0}.";
        }
        finally { IsBusy = false; }
    }

    private async Task AuditCitationsAsync()
    {
        IsBusy = true;
        try
        {
            CitationAudit = await _wiki.AuditCitationsAsync().ConfigureAwait(true);
            CitationAuditIssues.Clear();
            foreach (var issue in CitationAudit.Issues) CitationAuditIssues.Add(issue);
            RaisePropertyChanged(nameof(HasCitationAuditIssues));
            StatusText = CitationAudit.Summary;
        }
        finally { IsBusy = false; }
    }

    private async Task AuditQualityAsync()
    {
        IsBusy = true;
        try
        {
            QualityAudit = await _wiki.AuditQualityAsync().ConfigureAwait(true);
            CitationAudit = QualityAudit.Citations;
            CitationAuditIssues.Clear();
            foreach (var issue in QualityAudit.Citations.Issues) CitationAuditIssues.Add(issue);
            OrphanPages.Clear();
            foreach (var page in QualityAudit.OrphanPages) OrphanPages.Add(page);
            BrokenLinks.Clear();
            foreach (var link in QualityAudit.BrokenLinks) BrokenLinks.Add(link);
            DuplicatePages.Clear();
            foreach (var duplicate in QualityAudit.DuplicatePages) DuplicatePages.Add(duplicate);
            RaisePropertyChanged(nameof(HasCitationAuditIssues));
            RaisePropertyChanged(nameof(HasOrphans));
            RaisePropertyChanged(nameof(HasBrokenLinks));
            RaisePropertyChanged(nameof(HasDuplicates));
            StatusText = QualityAudit.Summary;
        }
        finally { IsBusy = false; }
    }

    private async Task AuditTopicsAsync()
    {
        IsBusy = true;
        try
        {
            TopicCleanup = await _wiki.AuditTopicsAsync().ConfigureAwait(true);
            TopicMergeSuggestions.Clear();
            foreach (var suggestion in TopicCleanup.Suggestions.Where(x => !x.SafeToAutomate)) TopicMergeSuggestions.Add(suggestion);
            TopicMergeHistory.Clear();
            foreach (var item in TopicCleanup.RecentMerges) TopicMergeHistory.Add(item);
            SelectedTopicSuggestion = TopicMergeSuggestions.FirstOrDefault();
            RaisePropertyChanged(nameof(HasTopicSuggestions));
            StatusText = TopicCleanup.Summary;
        }
        finally { IsBusy = false; }
    }

    private async Task RunAutomaticTopicCleanupAsync()
    {
        IsBusy = true;
        try
        {
            var result = await _wiki.RunAutomaticTopicCleanupAsync().ConfigureAwait(true);
            await LoadDashboardCollectionsAsync().ConfigureAwait(true);
            TopicCleanup = await _wiki.AuditTopicsAsync().ConfigureAwait(true);
            RefreshTopicCleanupCollections();
            StatusText = result.Summary;
        }
        finally { IsBusy = false; }
    }

    private async Task MergeSelectedTopicAsync()
    {
        if (SelectedTopicSuggestion is not { } suggestion) return;
        await MergeTopicSuggestionAsync(suggestion).ConfigureAwait(true);
    }

    private async Task MergeTopicSuggestionAsync(TopicMergeSuggestion suggestion)
    {
        IsBusy = true;
        try
        {
            var result = await _wiki.MergeTopicsAsync(new TopicMergeRequest(suggestion.CanonicalName, suggestion.Variants,
                suggestion.Confidence, suggestion.Reason, false, Environment.UserName)).ConfigureAwait(true);
            await LoadDashboardCollectionsAsync().ConfigureAwait(true);
            TopicCleanup = await _wiki.AuditTopicsAsync().ConfigureAwait(true);
            RefreshTopicCleanupCollections();
            StatusText = result.Summary;
        }
        finally { IsBusy = false; }
    }

    private void RefreshTopicCleanupCollections()
    {
        TopicMergeSuggestions.Clear();
        foreach (var suggestion in TopicCleanup?.Suggestions.Where(x => !x.SafeToAutomate) ?? Array.Empty<TopicMergeSuggestion>())
            TopicMergeSuggestions.Add(suggestion);
        TopicMergeHistory.Clear();
        foreach (var item in TopicCleanup?.RecentMerges ?? Array.Empty<TopicMergeHistoryRecord>()) TopicMergeHistory.Add(item);
        SelectedTopicSuggestion = TopicMergeSuggestions.FirstOrDefault();
        RaisePropertyChanged(nameof(HasTopicSuggestions));
    }

    private async Task LoadTimelineShowAsync(WikiTimelineShowSummary show)
    {
        try
        {
            IsBusy = true;
            var document = await _wiki.GetPageAsync(show.Page.PageId).ConfigureAwait(true);
            TimelineExplorerEvents.Clear();
            if (document is not null)
                foreach (var item in document.Timeline.OrderBy(x => x.StartDate).ThenBy(x => x.SortOrder))
                    TimelineExplorerEvents.Add(new WikiTimelineExplorerEvent(show.Page, item));
            var years = TimelineExplorerEvents.Where(x => x.Event.StartDate.HasValue).Select(x => x.Event.StartDate!.Value.Year).ToArray();
            TimelineMinimumYear = years.Length == 0 ? DateTime.Today.Year : years.Min();
            TimelineMaximumYear = years.Length == 0 ? DateTime.Today.Year : years.Max();
            TimelineYear = TimelineMinimumYear;
            FilterTimelineEvents();
            RaisePropertyChanged(nameof(TimelineYearText));
            StatusText = $"Loaded {TimelineExplorerEvents.Count:N0} timeline events for {show.Page.Title}.";
        }
        catch (Exception exception) { SetError(exception); }
        finally { IsBusy = false; }
    }

    private void FilterTimelineEvents()
    {
        FilteredTimelineEvents.Clear();
        if (TimelineExplorerEvents.Count == 0)
        {
            RaisePropertyChanged(nameof(HasTimelineExplorerEvents));
            return;
        }
        foreach (var item in TimelineExplorerEvents
                     .OrderBy(x => x.Event.StartDate ?? DateOnly.MaxValue)
                     .ThenBy(x => x.Event.SortOrder))
            FilteredTimelineEvents.Add(item);
        RaisePropertyChanged(nameof(HasTimelineExplorerEvents));
    }

    private static string BuildRevisionComparison(WikiRevisionRecord? revision, WikiPageDocument? current)
    {
        if (revision is null || current is null) return "Choose a revision to compare it with the current article.";
        if (revision.Revision == current.Revision) return "This is the current revision.";
        var oldLines = revision.BodyMarkdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var currentLines = current.BodyMarkdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var removed = oldLines.Except(currentLines, StringComparer.Ordinal).Count();
        var added = currentLines.Except(oldLines, StringComparer.Ordinal).Count();
        return $"Comparing revision {revision.Revision:N0} with current revision {current.Revision:N0}: {added:N0} current-only lines and {removed:N0} historical-only lines.\n\nHISTORICAL ARTICLE\n{revision.BodyMarkdown}";
    }

    private void UpdateSearchSuggestions()
    {
        SearchSuggestions.Clear();
        var query = (SearchText ?? string.Empty).Trim();
        if (query.Length < 2)
        {
            RaisePropertyChanged(nameof(HasSearchSuggestions));
            return;
        }
        foreach (var page in _allPages
                     .Where(x => x.Title.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                 x.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                 x.PageType.Contains(query, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(x => x.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                     .ThenBy(x => x.Title.Length)
                     .Take(8))
            SearchSuggestions.Add(page);
        RaisePropertyChanged(nameof(HasSearchSuggestions));
    }

    private void EnterMode(WikiNavigationMode mode, bool recordHistory)
    {
        var current = CurrentNavigationEntry();
        var target = new WikiNavigationEntry(mode, null);
        if (recordHistory && current != target) PushCurrentToBackHistory();
        IsDashboardMode = mode == WikiNavigationMode.Dashboard;
        IsBrowseMode = mode == WikiNavigationMode.Browse;
        IsTimelineExplorerMode = mode == WikiNavigationMode.Timeline;
    }

    private WikiNavigationEntry CurrentNavigationEntry()
    {
        if (IsDashboardMode) return new WikiNavigationEntry(WikiNavigationMode.Dashboard, null);
        if (IsBrowseMode) return new WikiNavigationEntry(WikiNavigationMode.Browse, null);
        if (IsTimelineExplorerMode) return new WikiNavigationEntry(WikiNavigationMode.Timeline, null);
        return new WikiNavigationEntry(WikiNavigationMode.Article, _selectedPage?.PageId);
    }

    private void PushCurrentToBackHistory()
    {
        var current = CurrentNavigationEntry();
        if (current.Mode == WikiNavigationMode.Article && current.PageId is null) return;
        if (_backHistory.Count == 0 || _backHistory.Peek() != current) _backHistory.Push(current);
        _forwardHistory.Clear();
        RaiseNavigationState();
    }

    private async Task NavigateBackAsync()
    {
        if (_backHistory.Count == 0) return;
        var target = _backHistory.Pop();
        _forwardHistory.Push(CurrentNavigationEntry());
        await ApplyNavigationEntryAsync(target).ConfigureAwait(true);
        RaiseNavigationState();
    }

    private async Task NavigateForwardAsync()
    {
        if (_forwardHistory.Count == 0) return;
        var target = _forwardHistory.Pop();
        _backHistory.Push(CurrentNavigationEntry());
        await ApplyNavigationEntryAsync(target).ConfigureAwait(true);
        RaiseNavigationState();
    }

    private async Task ApplyNavigationEntryAsync(WikiNavigationEntry target)
    {
        if (target.Mode == WikiNavigationMode.Article && target.PageId is { } pageId)
        {
            var page = _allPages.FirstOrDefault(x => x.PageId == pageId)
                ?? (await _wiki.BrowseAsync(new WikiBrowseQuery(Limit: 5000)).ConfigureAwait(true)).FirstOrDefault(x => x.PageId == pageId);
            if (page is not null) await OpenPageAsync(page, false).ConfigureAwait(true);
            return;
        }
        EnterMode(target.Mode, false);
        IsReadingMode = true;
    }

    private void RaiseNavigationState()
    {
        RaisePropertyChanged(nameof(CanGoBack));
        RaisePropertyChanged(nameof(CanGoForward));
        if (BackCommand is AsyncCommand back) back.RaiseCanExecuteChanged();
        if (ForwardCommand is AsyncCommand forward) forward.RaiseCanExecuteChanged();
    }

    private void RaiseModeState()
    {
        RaisePropertyChanged(nameof(IsExplorerMode));
        RaisePropertyChanged(nameof(IsArticleMode));
    }

    private void RaiseReaderState()
    {
        RaisePropertyChanged(nameof(ReaderBodyText));
        RaisePropertyChanged(nameof(ReaderByline));
        RaisePropertyChanged(nameof(ReaderAliases));
        RaisePropertyChanged(nameof(ReaderEvidence));
        RaisePropertyChanged(nameof(ReaderTimelineRange));
    }

    private static string FormatMarkdownForReading(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "This page has no article text yet.";
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        return string.Join(Environment.NewLine, lines.Select(line =>
        {
            var text = line.TrimEnd();
            if (text.StartsWith("### ", StringComparison.Ordinal)) return text[4..].ToUpperInvariant();
            if (text.StartsWith("## ", StringComparison.Ordinal)) return Environment.NewLine + text[3..].ToUpperInvariant();
            if (text.StartsWith("# ", StringComparison.Ordinal)) return string.Empty;
            if (text.StartsWith("- ", StringComparison.Ordinal)) return "• " + text[2..];
            return text.Replace("**", string.Empty, StringComparison.Ordinal).Replace("__", string.Empty, StringComparison.Ordinal);
        })).Trim();
    }

    private void RaiseCommandState()
    {
        _saveCommand.RaiseCanExecuteChanged();
        _revertCommand.RaiseCanExecuteChanged();
        _chooseImportCommand.RaiseCanExecuteChanged();
        _applyImportCommand.RaiseCanExecuteChanged();
        _cancelImportCommand.RaiseCanExecuteChanged();
        _exportCommand.RaiseCanExecuteChanged();
        _generateStartersCommand.RaiseCanExecuteChanged();
        _saveCitationCommand.RaiseCanExecuteChanged();
        _removeCitationCommand.RaiseCanExecuteChanged();
        _chooseImageCommand.RaiseCanExecuteChanged();
        _saveImageCommand.RaiseCanExecuteChanged();
        _removeImageCommand.RaiseCanExecuteChanged();
        _saveTimelineCommand.RaiseCanExecuteChanged();
        _removeTimelineCommand.RaiseCanExecuteChanged();
        _searchArchiveCommand.RaiseCanExecuteChanged();
        if (AuditTopicsCommand is AsyncCommand auditTopics) auditTopics.RaiseCanExecuteChanged();
        if (AutomaticTopicCleanupCommand is AsyncCommand automaticTopics) automaticTopics.RaiseCanExecuteChanged();
        if (MergeSelectedTopicCommand is AsyncCommand mergeTopics) mergeTopics.RaiseCanExecuteChanged();
    }

    private void SetError(Exception exception) => StatusText = string.IsNullOrWhiteSpace(exception.Message)
        ? "The wiki operation could not be completed."
        : exception.Message;
}

public sealed record WikiReaderImageViewModel(WikiPageImageLink Link, byte[] Content)
{
    public string Caption => Link.Image?.Caption ?? string.Empty;
    public string AltText => Link.Image?.AltText ?? string.Empty;
    public string DateText
    {
        get
        {
            var image = Link.Image;
            if (image is null) return string.Empty;
            if (image.CapturedDate is { } captured) return $"Captured {captured:dd MMM yyyy}";
            if (image.RepresentativeFrom is { } from && image.RepresentativeTo is { } to) return $"Represents {from:dd MMM yyyy} to {to:dd MMM yyyy}";
            if (image.RepresentativeFrom is { } start) return $"Represents the period from {start:dd MMM yyyy}";
            return image.DateNotes;
        }
    }
    public string CreditText => string.Join(" · ", new[] { Link.Image?.Creator, Link.Image?.Licence }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

public sealed record WikiTimelineExplorerEvent(WikiPageSummary Page, WikiTimelineEventRecord Event)
{
    public string YearText => Event.YearText;
    public string DateDisplay => Event.DateDisplay;
    public string Title => Event.Title;
    public string Summary => Event.Summary;
    public IReadOnlyList<WikiTimelineBroadcastLink> Broadcasts => Event.Broadcasts;
    public string EvidenceSummary => Event.EvidenceSummary;
}

internal enum WikiNavigationMode { Dashboard, Browse, Article, Timeline }
internal sealed record WikiNavigationEntry(WikiNavigationMode Mode, Guid? PageId);
