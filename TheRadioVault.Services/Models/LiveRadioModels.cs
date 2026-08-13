namespace TheRadioVault.Services.Models;

public sealed record LiveRadioProgramme(
    long ScheduleEntryId,
    DateTimeOffset StartsAt,
    DateTimeOffset EndsAt,
    long PositionMs,
    string SelectionReason,
    LibraryBroadcastSummary Broadcast)
{
    public long RemainingMs => Math.Max(0, (long)(EndsAt - StartsAt).TotalMilliseconds - PositionMs);
}

public sealed record LiveRadioSnapshot(
    string StationKey,
    string StationName,
    string TimeZoneId,
    DateTimeOffset ServerTime,
    long ScheduleRevision,
    LiveRadioProgramme? Current,
    IReadOnlyList<LiveRadioProgramme> Upcoming);
