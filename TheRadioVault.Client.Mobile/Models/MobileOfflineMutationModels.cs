namespace TheRadioVault.Client.Mobile.Models;

public enum MobileOfflineMutationKind
{
    Favourite,
    ListeningStatus,
    Moment
}

public sealed record MobileOfflineMutation(
    Guid Id,
    MobileOfflineMutationKind Kind,
    string ServerInstanceId,
    long EpisodeId,
    DateTimeOffset CapturedAt,
    bool? BooleanValue = null,
    long PositionMs = 0,
    string Title = "",
    string Notes = "",
    string MutationId = "",
    int Attempts = 0,
    string LastError = "");

public sealed class MobileOfflineMutationIndex
{
    public List<MobileOfflineMutation> Pending { get; init; } = [];
    public DateTimeOffset? LastAttemptAt { get; init; }
    public DateTimeOffset? LastSuccessfulSyncAt { get; init; }
    public string LastError { get; init; } = "";
}

public sealed record MobileSyncDiagnostics(
    int PendingChanges,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastSuccessfulSyncAt,
    string LastError);
