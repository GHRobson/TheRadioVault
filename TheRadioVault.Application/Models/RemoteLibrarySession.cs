namespace TheRadioVault.Application.Models;

public enum RemoteLibrarySessionState
{
    Disconnected,
    Connecting,
    Updating,
    Live,
    CachedReadOnly,
    Unavailable,
    Closing
}

public sealed record RemoteLibrarySyncCursor(
    string SessionId,
    long Sequence,
    string LibraryRevision)
{
    public static RemoteLibrarySyncCursor Empty { get; } = new(string.Empty, 0, string.Empty);

    public RemoteLibrarySyncCursor Normalize()
        => new(
            SessionId?.Trim() ?? string.Empty,
            Math.Max(0, Sequence),
            LibraryRevision?.Trim() ?? string.Empty);
}

public sealed record RemoteLibrarySyncRequest(
    bool InitialLoad,
    bool ForceReset,
    bool Silent,
    TimeSpan Timeout)
{
    public static RemoteLibrarySyncRequest Create(
        bool initialLoad,
        bool forceReset,
        bool silent,
        TimeSpan? timeout = null)
        => new(
            initialLoad,
            forceReset,
            silent,
            timeout ?? TimeSpan.FromSeconds(initialLoad ? 45 : 25));
}

public sealed record RemoteLibrarySyncMetrics(
    string Mode,
    int ChangeEvents,
    int ChangedRows,
    int DeletedRows,
    bool NoChanges,
    bool ResetRequired,
    long ElapsedMilliseconds)
{
    public RemoteLibrarySyncMetrics Normalize()
        => this with
        {
            Mode = string.IsNullOrWhiteSpace(Mode) ? "synchronization" : Mode.Trim(),
            ChangeEvents = Math.Max(0, ChangeEvents),
            ChangedRows = Math.Max(0, ChangedRows),
            DeletedRows = Math.Max(0, DeletedRows),
            ElapsedMilliseconds = Math.Max(0, ElapsedMilliseconds)
        };
}

public sealed record RemoteLibrarySyncDiagnostics(
    DateTimeOffset? LastCompletedAt,
    string LastMode,
    long LastDurationMs,
    int LastChangedRows,
    string LastError)
{
    public static RemoteLibrarySyncDiagnostics Empty { get; } = new(
        null,
        "not yet synchronized",
        0,
        0,
        string.Empty);
}

public sealed record RemoteLibrarySessionSnapshot(
    RemoteLibrarySessionState State,
    RemoteLibrarySyncCursor Cursor,
    DateTimeOffset? LastLiveAt,
    int ConsecutiveFailures,
    DateTimeOffset? NextReconnectAt,
    bool IsSyncInProgress,
    bool HasUsableSnapshot,
    bool IsCacheOnly,
    RemoteLibrarySyncDiagnostics Diagnostics)
{
    public bool IsCachedReadOnly => IsCacheOnly;
    public bool IsLive => State == RemoteLibrarySessionState.Live;
    public bool IsClosing => State == RemoteLibrarySessionState.Closing;
}

public sealed record RemoteLibraryFailurePlan(
    RemoteLibrarySessionSnapshot Snapshot,
    TimeSpan RetryDelay,
    bool HasUsableSnapshot);
