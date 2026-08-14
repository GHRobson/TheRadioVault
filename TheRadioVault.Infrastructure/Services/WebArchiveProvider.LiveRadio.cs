using TheRadioVault.Services.Models;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

internal sealed partial class WebArchiveProvider
{
    public WebLiveRadioSnapshot GetLiveRadioSnapshot()
    {
        var value = _liveRadio.GetSnapshotAsync().GetAwaiter().GetResult();
        return new WebLiveRadioSnapshot(
            value.StationKey,
            value.StationName,
            value.TimeZoneId,
            value.ServerTime,
            value.ScheduleRevision,
            value.Current is null ? null : MapLiveProgramme(value.Current),
            value.Upcoming.Select(MapLiveProgramme).ToArray());
    }

    private static WebLiveRadioProgramme MapLiveProgramme(LiveRadioProgramme value)
        => new(
            value.ScheduleEntryId,
            value.StartsAt,
            value.EndsAt,
            value.PositionMs,
            value.RemainingMs,
            value.SelectionReason,
            Map(value.Broadcast));
}
