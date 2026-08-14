namespace TheRadioVault.Services.Models;

public sealed record RssFeedSubscription(
    long Id,
    string Name,
    string DisplayUrl,
    long LibraryFolderId,
    string DestinationPath,
    string CollectionName,
    int CheckIntervalMinutes,
    bool Enabled,
    bool DestinationEnabled,
    bool ImportExistingOnFirstCheck,
    bool Initialized,
    DateTimeOffset? LastCheckedAt,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? NextCheckAt,
    string LastError,
    int DownloadedCount,
    int SeenCount)
{
    public string IntervalText => CheckIntervalMinutes < 60
        ? $"Every {CheckIntervalMinutes} minutes"
        : CheckIntervalMinutes % 1440 == 0
            ? $"Every {CheckIntervalMinutes / 1440} day{(CheckIntervalMinutes == 1440 ? string.Empty : "s")}"
            : $"Every {CheckIntervalMinutes / 60d:0.#} hours";

    public string LastCheckText => LastCheckedAt.HasValue
        ? $"Last checked {LastCheckedAt.Value.ToLocalTime():dd MMM yyyy HH:mm}"
        : "Not checked yet";

    public string StatusText => !DestinationEnabled
        ? "Destination folder is disabled"
        : !Enabled
            ? "Feed is paused"
            : !string.IsNullOrWhiteSpace(LastError)
                ? LastError
                : DownloadedCount == 0
                    ? LastCheckText
                    : $"{DownloadedCount:N0} broadcast{(DownloadedCount == 1 ? string.Empty : "s")} downloaded · {LastCheckText}";
}

public sealed record RssFeedSource(string FeedUrl, string Username = "", string Password = "");

public sealed record RssFeedSaveRequest(
    string Name,
    RssFeedSource Source,
    long LibraryFolderId,
    int CheckIntervalMinutes = 30,
    bool Enabled = true,
    bool ImportExistingOnFirstCheck = false);

public sealed record RssFeedCheckResult(
    int FeedsChecked,
    int NewDownloads,
    int AlreadyKnown,
    int FailedItems,
    bool LibraryScanStarted,
    string Message);

internal sealed record RssFeedSubscriptionState(
    RssFeedSubscription Subscription,
    string ProtectedSource,
    string? ETag,
    string? LastModified);

internal sealed record RssFeedItemCandidate(
    string StableKey,
    string Title,
    DateTimeOffset? PublishedAt,
    string EnclosureHash);

internal sealed record RssFeedItemRegistration(long Id, string Status, bool WasAdded, bool ShouldDownload);
