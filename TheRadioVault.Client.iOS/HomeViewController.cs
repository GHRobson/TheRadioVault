using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class HomeViewController : SessionTableViewController
{
    private string[] _sectionFingerprints = new string[6];

    public HomeViewController(MobileClientSession session) : base(session)
    {
        Title = "Dashboard";
        TabBarItem.AccessibilityLabel = "Dashboard";
    }

    private MobileBroadcastItem? FeaturedContinue
    {
        get
        {
            var current = Session.CurrentBroadcast;
            return current is not null && !current.Source.Completed &&
                   (Session.MiniPlayerShowsHandoff || Session.CanControlPlayback)
                ? current
                : Session.ContinueListening.FirstOrDefault();
        }
    }

    private IReadOnlyList<MobileBroadcastItem> UpNext
    {
        get
        {
            var featuredId = FeaturedContinue?.EpisodeId;
            return Session.ContinueListening
                .Where(value => value.EpisodeId != featuredId)
                .Take(4)
                .ToArray();
        }
    }

    private IReadOnlyList<MobileBroadcastItem> RecentlyAdded
        => Session.RecentBroadcasts.Take(5).ToArray();

    private IReadOnlyList<MobileBroadcastItem> Unheard
        => Session.UnheardBroadcasts.Take(5).ToArray();

    protected override string? PageHeading => "Dashboard";
    protected override string PageDescription => "Your archive at a glance.";

    public override void ViewDidLoad()
    {
        base.ViewDidLoad();
        _sectionFingerprints = CaptureSectionFingerprints();
        NavigationItem.LargeTitleDisplayMode = UINavigationItemLargeTitleDisplayMode.Never;
        TableView.RowHeight = UITableView.AutomaticDimension;
        TableView.EstimatedRowHeight = 88;
        RefreshControl = new UIRefreshControl();
        RefreshControl.ValueChanged += async (_, _) =>
        {
            await Session.RefreshAsync();
            BeginInvokeOnMainThread(() => RefreshControl?.EndRefreshing());
        };
        var settings = UIButton.FromType(UIButtonType.System);
        settings.SetImage(RadioVaultIcons.Image(RadioVaultIcon.Settings), UIControlState.Normal);
        settings.BackgroundColor = RadioVaultTheme.SurfaceRaised;
        settings.Layer.CornerRadius = 19;
        settings.WidthAnchor.ConstraintEqualTo(38).Active = true;
        settings.HeightAnchor.ConstraintEqualTo(38).Active = true;
        settings.TouchUpInside += (_, _) =>
            NavigationController?.PushViewController(new ServerViewController(Session), true);
        settings.AccessibilityLabel = "Settings";
        if (TableView.TableHeaderView is PageHeaderView header)
        {
            header.SetAccessory(settings);
        }
    }

    public override nint NumberOfSections(UITableView tableView) => 6;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 1,
        1 => 1,
        2 => Math.Max(1, UpNext.Count),
        3 => 1,
        4 => Math.Max(1, RecentlyAdded.Count),
        5 => Math.Max(1, Unheard.Count),
        _ => 0
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        1 => "Continue listening",
        2 => "Up next",
        3 => "On this day",
        4 => "Recently added",
        5 => "Unheard broadcasts",
        _ => null
    };

    public override string? TitleForFooter(UITableView tableView, nint section) => section switch
    {
        1 when FeaturedContinue is null => "Choose something from the Library or let Radio Vault pick for you.",
        _ => null
    };

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        if (indexPath.Section == 0)
        {
            var overview = new DashboardOverviewCell();
            overview.Configure(
                Session.UnheardBroadcasts.Count > 0,
                () =>
                {
                    var pool = Session.UnheardBroadcasts;
                    if (pool.Count > 0) _ = Session.PlayAsync(pool[Random.Shared.Next(pool.Count)]);
                },
                ("Broadcasts", Session.TotalBroadcasts, RadioVaultIcon.Library,
                    () => OpenLibrarySection("All Broadcasts", "All")),
                ("In progress", Session.InProgressBroadcasts, RadioVaultIcon.InProgress,
                    () => OpenLibrarySection("Continue Listening", "ContinueListening")),
                ("Completed", Session.CompletedBroadcasts, RadioVaultIcon.Completed,
                    () => OpenLibrarySection("Completed", "Completed")),
                ("Favourites", Session.FavouriteBroadcasts, RadioVaultIcon.Favourite,
                    () => OpenLibrarySection("Favourites", "Favourites")));
            return overview;
        }

        if (indexPath.Section == 1)
        {
            if (FeaturedContinue is not { } featured)
                return DetailCell("dashboard-featured-empty", "Nothing waiting to resume", Session.StatusText);
            var cell = new DashboardContinueCell();
            cell.Configure(
                Session,
                featured,
                Session.PreparingPlaybackEpisodeId == featured.EpisodeId,
                Session.IsPlayingBroadcast(featured.EpisodeId),
                () => HandleFeaturedPlayback(featured));
            return cell;
        }

        if (indexPath.Section == 3 && Session.OnThisDay.Count > 0)
        {
            var carousel = new DashboardOnThisDayCarouselCell();
            carousel.Configure(
                Session,
                Session.OnThisDay,
                item => Session.LoadBroadcastDetailsAsync(item, announce: false),
                item => NavigationController?.PushViewController(new BroadcastDetailsViewController(Session, item), true),
                entity => _ = OpenEntityAsync(entity));
            return carousel;
        }

        var values = ValuesForSection(indexPath.Section);
        if (values.Count == 0)
        {
            var empty = indexPath.Section switch
            {
                2 => "Nothing else waiting to resume",
                3 => "No broadcasts aired on this date",
                4 => "No recently added broadcasts",
                _ => "You have heard everything in the Library"
            };
            return DetailCell("dashboard-empty", empty, "Pull down to refresh the Dashboard.");
        }

        var item = values[indexPath.Row];
        var broadcastCell = new BroadcastProgressCell("dashboard-broadcast");
        broadcastCell.Configure(Session, item, indexPath.Section == 2
            ? $"{item.Source.CollectionName} · ready to resume"
            : $"{item.Subtitle} · {item.Status}");
        broadcastCell.Accessory = UITableViewCellAccessory.DisclosureIndicator;
        return broadcastCell;
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Section == 1 && FeaturedContinue is { } featured)
        {
            HandleFeaturedPlayback(featured);
            return;
        }
        if (indexPath.Section < 2 || indexPath.Section == 3) return;
        var values = ValuesForSection(indexPath.Section);
        if (indexPath.Row < values.Count)
            NavigationController?.PushViewController(
                new BroadcastDetailsViewController(Session, values[indexPath.Row]), true);
    }

    protected override MobileBroadcastItem? ContextBroadcastForRow(NSIndexPath indexPath)
    {
        if (indexPath.Section == 1) return FeaturedContinue;
        if (indexPath.Section < 2 || indexPath.Section == 3) return null;
        var values = ValuesForSection(indexPath.Section);
        return indexPath.Row < values.Count ? values[indexPath.Row] : null;
    }

    private IReadOnlyList<MobileBroadcastItem> ValuesForSection(nint section) => section switch
    {
        2 => UpNext,
        3 => Session.OnThisDay,
        4 => RecentlyAdded,
        5 => Unheard,
        _ => []
    };

    private void OpenLibrarySection(string title, string filter)
    {
        if (TabBarController?.ViewControllers is not { Length: > 1 } controllers ||
            controllers[1] is not UINavigationController libraryNavigation) return;
        TabBarController.SelectedIndex = 1;
        libraryNavigation.PopToRootViewController(false);
        libraryNavigation.PushViewController(
            new ShowLibraryViewController(Session, null, title, filter),
            true);
    }

    private void HandleFeaturedPlayback(MobileBroadcastItem featured)
    {
        if (Session.PreparingPlaybackEpisodeId == featured.EpisodeId) return;
        if (Session.CanToggleBroadcast(featured.EpisodeId)) Session.TogglePlayPause();
        else _ = Session.PlayAsync(featured);
    }

    protected override void ReloadSession()
    {
        var next = CaptureSectionFingerprints();
        using var changedSections = new NSMutableIndexSet();
        for (var section = 0; section < next.Length; section++)
        {
            if (!string.Equals(_sectionFingerprints[section], next[section], StringComparison.Ordinal))
            {
                _sectionFingerprints[section] = next[section];
                changedSections.Add((nuint)section);
            }
            else if (section is 2 or 4 or 5)
            {
                RefreshVisibleBroadcasts(section);
            }
        }

        // UIKit validates all row-count changes as one transaction. Reloading
        // changed sections individually can crash while the initial sync fills
        // several Dashboard lists at the same time.
        if (changedSections.Count > 0)
            TableView.ReloadSections(changedSections, UITableViewRowAnimation.None);
    }

    private string[] CaptureSectionFingerprints()
    {
        var featured = FeaturedContinue;
        return
        [
            $"{Session.TotalBroadcasts}:{Session.InProgressBroadcasts}:{Session.CompletedBroadcasts}:{Session.FavouriteBroadcasts}:{Session.UnheardBroadcasts.Count}",
            featured is null
                ? "empty"
                : $"{BroadcastFingerprint([featured])}:{Session.PreparingPlaybackEpisodeId}:{Session.IsPlayingBroadcast(featured.EpisodeId)}",
            BroadcastIdentityFingerprint(UpNext),
            string.Join(",", Session.OnThisDay.Select(value => value.EpisodeId).Order()),
            BroadcastIdentityFingerprint(RecentlyAdded),
            BroadcastIdentityFingerprint(Unheard)
        ];
    }

    private static string BroadcastFingerprint(IEnumerable<MobileBroadcastItem> values)
        => string.Join("|", values.Select(value =>
            $"{value.EpisodeId}:{value.DisplayProgress:0.###}:{value.Source.Completed}:{value.Source.Favourite}:{value.Status}"));

    private static string BroadcastIdentityFingerprint(IEnumerable<MobileBroadcastItem> values)
        => string.Join(",", values.Select(value => value.EpisodeId));

    private void RefreshVisibleBroadcasts(int section)
    {
        var values = ValuesForSection(section);
        for (var row = 0; row < values.Count; row++)
        {
            if (TableView.CellAt(NSIndexPath.FromRowSection(row, section)) is not BroadcastProgressCell cell) continue;
            var item = values[row];
            cell.Configure(Session, item, section == 2
                ? $"{item.Source.CollectionName} · ready to resume"
                : $"{item.Subtitle} · {item.Status}");
        }
    }

    private async Task OpenEntityAsync(string entity)
    {
        var dashboard = await Session.LoadExploreDashboardAsync().ConfigureAwait(false);
        var page = dashboard?.AllPages.FirstOrDefault(value =>
            value.Title.Equals(entity, StringComparison.CurrentCultureIgnoreCase) ||
            value.Slug.Equals(entity.Replace(' ', '-'), StringComparison.OrdinalIgnoreCase));
        BeginInvokeOnMainThread(() => NavigationController?.PushViewController(
            page is not null
                ? new ExploreArticleViewController(Session, page)
                : new EntityBroadcastsViewController(Session, entity),
            true));
    }
}
