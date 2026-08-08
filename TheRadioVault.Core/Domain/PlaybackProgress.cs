namespace TheRadioVault.Core.Domain;

public enum ListeningStatus
{
    Unplayed,
    InProgress,
    Completed
}

public sealed record PlaybackProgress(
    long PositionMs,
    long DurationMs,
    bool Completed = false)
{
    public double Percent => Services.PlaybackProgressService.CalculatePercent(PositionMs, DurationMs);
    public ListeningStatus Status => Services.PlaybackProgressService.GetStatus(PositionMs, DurationMs, Completed);
}
