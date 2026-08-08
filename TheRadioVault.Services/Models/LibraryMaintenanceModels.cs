namespace TheRadioVault.Services.Models;

public sealed record LibraryMaintenanceSnapshot(
    bool IsRunning,
    bool Started,
    string Trigger,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string Message,
    int FilesFound,
    int Added,
    int Updated,
    int Unchanged,
    int Errors,
    int CanonicalBroadcastsAdded,
    int CanonicalRecordingsAdded,
    int CanonicalEpisodesMapped,
    int CanonicalItemsNeedingReview)
{
    public bool HasCompletedScan => CompletedAt.HasValue;
    public bool HasChanges => Added > 0 || Updated > 0 || CanonicalBroadcastsAdded > 0 || CanonicalEpisodesMapped > 0;
    public string StatusText => IsRunning
        ? string.IsNullOrWhiteSpace(Message) ? "Scanning the server Library…" : Message
        : CompletedAt.HasValue
            ? $"Last scan {CompletedAt.Value.ToLocalTime():dd MMM yyyy HH:mm}"
            : "No scan has completed in this session.";

    public string ResultText => !CompletedAt.HasValue
        ? "Radio Vault checks registered archive folders automatically every hour."
        : $"{FilesFound:N0} found · {Added:N0} added · {Updated:N0} updated · {Unchanged:N0} unchanged · {Errors:N0} errors";
}
