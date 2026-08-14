using System.Text;
using System.Reflection;
using CoreGraphics;
using Foundation;
using TheRadioVault.Client.Mobile;
using TheRadioVault.Client.Mobile.Models;
using UIKit;

namespace TheRadioVault.Client.iOS;

public sealed class SyncDiagnosticsViewController : SessionTableViewController
{
    private MobileDiagnosticSnapshot? _snapshot;
    private bool _loading;

    public SyncDiagnosticsViewController(MobileClientSession session) : base(session) => Title = "Diagnostics";

    protected override string? PageHeading => "Diagnostics";
    protected override string PageDescription => "Connection, saved data, downloads and playback.";

    public override void ViewWillAppear(bool animated)
    {
        base.ViewWillAppear(animated);
        _ = LoadSnapshotAsync();
    }

    public override nint NumberOfSections(UITableView tableView) => 5;

    public override nint RowsInSection(UITableView tableView, nint section) => section switch
    {
        0 => 3,
        1 when _snapshot is null => 1,
        2 when _snapshot is null => 1,
        3 when _snapshot is null => 1,
        1 => string.IsNullOrWhiteSpace(_snapshot?.LastSyncError) ? 5 : 6,
        2 => 4,
        3 => 4,
        _ => 2
    };

    public override string? TitleForHeader(UITableView tableView, nint section) => section switch
    {
        0 => "App & Device",
        1 => "Connection & Sync",
        2 => "Saved on this iPhone",
        3 => "Downloads & Playback",
        _ => "Actions"
    };

    public override UITableViewCell GetCell(UITableView tableView, NSIndexPath indexPath)
    {
        var snapshot = _snapshot;
        if (indexPath.Section == 0)
            return indexPath.Row switch
            {
                0 => DetailCell("diagnostic-version", "Radio Vault", AppVersion()),
                1 => DetailCell("diagnostic-ios", "iOS", UIDevice.CurrentDevice.SystemVersion),
                _ => DetailCell("diagnostic-device", "Device", UIDevice.CurrentDevice.Model)
            };

        if (indexPath.Section == 1)
        {
            if (snapshot is null) return DetailCell("diagnostic-loading", "Loading…", "Reading the saved state");
            return indexPath.Row switch
            {
                0 => DetailCell("diagnostic-connection", "Connection", snapshot.IsLiveConnected ? "Live connection" : "Offline · using saved data"),
                1 => DetailCell("diagnostic-server", "Paired Server", snapshot.IsPaired ? $"{snapshot.ServerName}\n{snapshot.ServerAddress}" : "Not paired"),
                2 => DetailCell("diagnostic-pending", "Pending Changes", PendingText(snapshot)),
                3 => DetailCell("diagnostic-success", "Last Successful Sync", FormatDate(snapshot.LastSuccessfulSyncAt)),
                4 => DetailCell("diagnostic-attempt", "Last Attempt", FormatDate(snapshot.LastSyncAttemptAt)),
                _ => DetailCell("diagnostic-error", "Last Error", snapshot.LastSyncError)
            };
        }

        if (indexPath.Section == 2)
        {
            if (snapshot is null) return DetailCell("diagnostic-cache-loading", "Loading…", "Reading the saved catalogue");
            return indexPath.Row switch
            {
                0 => DetailCell("diagnostic-library", "Library Catalogue", $"{snapshot.CachedBroadcasts:N0} broadcasts · {snapshot.CachedCollections:N0} shows"),
                1 => DetailCell("diagnostic-explore", "Explore Archive", $"{snapshot.CachedExplorePages:N0} pages · {snapshot.CachedExploreDocuments:N0} full articles · {snapshot.CachedExploreImages:N0} images"),
                2 => DetailCell("diagnostic-knowledge", "Saved & Knowledge", $"{snapshot.CachedMoments:N0} moments · knowledge {(snapshot.HasKnowledge ? "available" : "not cached")}"),
                _ => DetailCell("diagnostic-cache", "Catalogue Revision", CacheText(snapshot))
            };
        }

        if (indexPath.Section == 3)
        {
            if (snapshot is null) return DetailCell("diagnostic-playback-loading", "Loading…", "Reading downloads and playback");
            return indexPath.Row switch
            {
                0 => DetailCell("diagnostic-storage", "Downloaded Audio", $"{snapshot.DownloadCount:N0} broadcasts · {FormatBytes(snapshot.DownloadedBytes)}"),
                1 => DetailCell("diagnostic-download", "Download Activity", DownloadText(snapshot)),
                2 => DetailCell("diagnostic-playback", "Now Playing", PlaybackText(snapshot)),
                _ => DetailCell("diagnostic-owner", "Playback Control", PlaybackOwnerText(snapshot))
            };
        }

        if (indexPath.Row == 0)
        {
            var cell = IconDetailCell(
                "diagnostic-sync-now",
                Session.IsMetadataSyncing ? "Syncing…" : "Sync Now",
                "Upload saved changes and check the complete catalogue",
                RadioVaultIcon.Sync,
                true);
            cell.UserInteractionEnabled = !Session.IsMetadataSyncing;
            return cell;
        }

        return IconDetailCell(
            "diagnostic-export",
            "Export Diagnostic Report",
            "Share a text report and this launch's playback log",
            RadioVaultIcon.Download,
            true);
    }

    public override void RowSelected(UITableView tableView, NSIndexPath indexPath)
    {
        tableView.DeselectRow(indexPath, true);
        if (indexPath.Section != 4) return;
        if (indexPath.Row == 0 && !Session.IsMetadataSyncing) _ = SyncAndReloadAsync();
        else if (indexPath.Row == 1) ExportReport();
    }

    protected override void ReloadSession() => TableView.ReloadData();

    private async Task LoadSnapshotAsync()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var snapshot = await Session.GetDiagnosticSnapshotAsync().ConfigureAwait(false);
            BeginInvokeOnMainThread(() =>
            {
                _snapshot = snapshot;
                TableView.ReloadData();
            });
        }
        catch (Exception exception)
        {
            IosPlaybackDiagnostics.Write($"[RadioVault iOS diagnostic] Snapshot failed: {exception}");
        }
        finally { _loading = false; }
    }

    private async Task SyncAndReloadAsync()
    {
        try
        {
            await Session.RetrySyncAsync().ConfigureAwait(false);
            await LoadSnapshotAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            IosPlaybackDiagnostics.Write($"[RadioVault iOS diagnostic] Manual sync failed: {exception}");
            BeginInvokeOnMainThread(() => PresentMessage("Sync Failed", exception.Message));
        }
    }

    private void ExportReport()
    {
        if (_snapshot is null)
        {
            PresentMessage("Diagnostics Still Loading", "Please try again in a moment.");
            return;
        }

        try
        {
            var path = IosPlaybackDiagnostics.CreateExportFile(BuildReport(_snapshot));
            var activity = new UIActivityViewController([NSUrl.FromFilename(path)], null);
            if (activity.PopoverPresentationController is { } popover)
            {
                var sourceView = View!;
                popover.SourceView = sourceView;
                popover.SourceRect = new CGRect(sourceView.Bounds.GetMidX(), sourceView.Bounds.GetMidY(), 1, 1);
            }
            PresentViewController(activity, true, null);
        }
        catch (Exception exception)
        {
            IosPlaybackDiagnostics.Write($"[RadioVault iOS diagnostic] Export failed: {exception}");
            PresentMessage("Export Failed", exception.Message);
        }
    }

    private void PresentMessage(string title, string message)
    {
        var alert = UIAlertController.Create(title, message, UIAlertControllerStyle.Alert);
        alert.AddAction(UIAlertAction.Create("OK", UIAlertActionStyle.Default, null));
        PresentViewController(alert, true, null);
    }

    private static string BuildReport(MobileDiagnosticSnapshot snapshot)
    {
        var report = new StringBuilder();
        report.AppendLine("RADIO VAULT iOS DIAGNOSTIC REPORT");
        report.AppendLine("=================================");
        report.AppendLine($"Captured: {snapshot.CapturedAt.ToLocalTime():O}");
        report.AppendLine($"App: {AppVersion()}");
        report.AppendLine($"Device: {UIDevice.CurrentDevice.Model}");
        report.AppendLine($"iOS: {UIDevice.CurrentDevice.SystemVersion}");
        report.AppendLine();
        report.AppendLine("CONNECTION & SYNC");
        report.AppendLine($"Paired: {snapshot.IsPaired}");
        report.AppendLine($"Live connection: {snapshot.IsLiveConnected}");
        report.AppendLine($"Metadata syncing: {snapshot.IsMetadataSyncing}");
        report.AppendLine($"Server: {snapshot.ServerName} ({snapshot.ServerAddress})");
        report.AppendLine($"Status: {snapshot.StatusText}");
        report.AppendLine($"Pending changes: {PendingText(snapshot)}");
        report.AppendLine($"Last successful sync: {FormatDate(snapshot.LastSuccessfulSyncAt)}");
        report.AppendLine($"Last sync attempt: {FormatDate(snapshot.LastSyncAttemptAt)}");
        report.AppendLine($"Last sync error: {(string.IsNullOrWhiteSpace(snapshot.LastSyncError) ? "None" : snapshot.LastSyncError)}");
        report.AppendLine();
        report.AppendLine("SAVED DATA");
        report.AppendLine($"Cache updated: {FormatDate(snapshot.CacheUpdatedAt == DateTimeOffset.MinValue ? null : snapshot.CacheUpdatedAt)}");
        report.AppendLine($"Cache sequence: {snapshot.CacheSyncSequence}");
        report.AppendLine($"Cache revision: {snapshot.CacheSyncRevision}");
        report.AppendLine($"Broadcasts: {snapshot.CachedBroadcasts}");
        report.AppendLine($"Shows: {snapshot.CachedCollections}");
        report.AppendLine($"Explore pages/documents/images: {snapshot.CachedExplorePages}/{snapshot.CachedExploreDocuments}/{snapshot.CachedExploreImages}");
        report.AppendLine($"Moments: {snapshot.CachedMoments}");
        report.AppendLine($"Knowledge cached: {snapshot.HasKnowledge}");
        report.AppendLine();
        report.AppendLine("DOWNLOADS & PLAYBACK");
        report.AppendLine($"Downloads: {snapshot.DownloadCount} ({FormatBytes(snapshot.DownloadedBytes)})");
        report.AppendLine($"Pending download data: {FormatBytes(snapshot.PendingDownloadBytes)}");
        report.AppendLine($"Download active/paused: {snapshot.IsDownloading}/{snapshot.IsDownloadPaused}");
        report.AppendLine($"Active download episode: {snapshot.ActiveDownloadEpisodeId?.ToString() ?? "None"}");
        report.AppendLine($"Download status: {snapshot.DownloadStatus}");
        report.AppendLine($"Now-playing episode: {snapshot.CurrentEpisodeId?.ToString() ?? "None"}");
        report.AppendLine($"Playing: {snapshot.IsPlaying}");
        report.AppendLine($"Can control playback: {snapshot.CanControlPlayback}");
        report.AppendLine($"Showing handoff: {snapshot.MiniPlayerShowsHandoff}");
        report.AppendLine($"Playback status: {snapshot.PlaybackStatus}");
        report.AppendLine($"Playback time: {snapshot.PlaybackTime}");
        report.AppendLine();
        report.AppendLine("DOWNLOAD SETTINGS");
        report.AppendLine($"Wi-Fi only: {snapshot.WifiOnlyDownloads}");
        report.AppendLine($"Automatic downloads: {snapshot.AutoDownloadNewBroadcasts}");
        report.AppendLine($"Delete completed: {snapshot.DeleteCompletedDownloads}");
        report.AppendLine($"Download expiry: {(snapshot.DownloadExpiryDays <= 0 ? "Never" : snapshot.DownloadExpiryDays + " days")}");
        report.AppendLine($"Storage limit: {(snapshot.DownloadStorageLimitBytes <= 0 ? "None" : FormatBytes(snapshot.DownloadStorageLimitBytes))}");
        report.AppendLine();
        report.AppendLine("Privacy: access tokens, pairing codes and certificates are not included.");
        return report.ToString();
    }

    private static string AppVersion()
    {
        var version = NSBundle.MainBundle.ObjectForInfoDictionary("CFBundleShortVersionString")?.ToString() ?? "Unknown";
        var build = NSBundle.MainBundle.ObjectForInfoDictionary("CFBundleVersion")?.ToString() ?? "Unknown";
        var informational = typeof(SyncDiagnosticsViewController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var separator = informational?.IndexOf('+') ?? -1;
        var identity = separator >= 0 && separator < informational!.Length - 1
            ? informational[(separator + 1)..]
            : "unknown";
        if (identity is not ("local" or "unknown"))
        {
            var suffixSeparator = identity.IndexOf('.');
            var commit = suffixSeparator < 0 ? identity : identity[..suffixSeparator];
            var suffix = suffixSeparator < 0 ? string.Empty : identity[suffixSeparator..];
            identity = commit[..Math.Min(12, commit.Length)] + suffix;
        }
        return $"Version {version} ({build}) · build {identity}";
    }

    private static string PendingText(MobileDiagnosticSnapshot snapshot)
        => snapshot.PendingChanges == 0
            ? "None"
            : $"{snapshot.PendingChanges:N0} waiting · {snapshot.PendingFavouriteChanges:N0} favourites · " +
              $"{snapshot.PendingListeningChanges:N0} listening · {snapshot.PendingMomentChanges:N0} moments";

    private static string CacheText(MobileDiagnosticSnapshot snapshot)
    {
        var updated = snapshot.CacheUpdatedAt == DateTimeOffset.MinValue
            ? "Not yet saved"
            : snapshot.CacheUpdatedAt.ToLocalTime().ToString("g");
        var revision = string.IsNullOrWhiteSpace(snapshot.CacheSyncRevision)
            ? "no revision"
            : snapshot.CacheSyncRevision;
        return $"{updated} · sequence {snapshot.CacheSyncSequence:N0} · {revision}";
    }

    private static string DownloadText(MobileDiagnosticSnapshot snapshot)
    {
        if (snapshot.IsDownloading) return $"Downloading episode {snapshot.ActiveDownloadEpisodeId} · {snapshot.DownloadStatus}";
        if (snapshot.IsDownloadPaused) return $"Paused · {snapshot.DownloadStatus}";
        if (snapshot.PendingDownloadBytes > 0) return $"{FormatBytes(snapshot.PendingDownloadBytes)} waiting · {snapshot.DownloadStatus}";
        return snapshot.DownloadStatus;
    }

    private static string PlaybackText(MobileDiagnosticSnapshot snapshot)
        => snapshot.HasMiniPlayer
            ? $"Episode {snapshot.CurrentEpisodeId} · {snapshot.PlaybackTime}\n{snapshot.PlaybackStatus}"
            : "Nothing selected";

    private static string PlaybackOwnerText(MobileDiagnosticSnapshot snapshot)
    {
        if (snapshot.MiniPlayerShowsHandoff) return "Another device has control · handoff available";
        if (snapshot.CanControlPlayback) return snapshot.IsPlaying ? "This iPhone · playing" : "This iPhone · paused";
        return "No active playback owner";
    }

    private static string FormatDate(DateTimeOffset? value)
        => value is null ? "Not yet" : value.Value.ToLocalTime().ToString("g");

    private static string FormatBytes(long value)
    {
        var bytes = Math.Max(0, value);
        if (bytes >= 1024L * 1024L * 1024L) return $"{bytes / (1024d * 1024d * 1024d):0.##} GB";
        if (bytes >= 1024L * 1024L) return $"{bytes / (1024d * 1024d):0.##} MB";
        if (bytes >= 1024L) return $"{bytes / 1024d:0.##} KB";
        return $"{bytes:N0} bytes";
    }
}
