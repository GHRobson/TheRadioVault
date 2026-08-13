using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

public sealed class LoopbackLiveRadioService : ILiveRadioService
{
    private readonly LoopbackServerClient _connection;

    public LoopbackLiveRadioService(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<LiveRadioSnapshot> GetSnapshotAsync(
        DateTimeOffset? at = null,
        CancellationToken cancellationToken = default)
    {
        var envelope = await _connection.SendJsonAsync<LiveRadioEnvelope>(
            HttpMethod.Get,
            WebApiRoutes.ClientLiveRadio,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return Map(envelope.Station);
    }

    private static LiveRadioSnapshot Map(WebLiveRadioSnapshot value)
        => new(
            value.StationKey,
            value.StationName,
            value.TimeZoneId,
            value.ServerTime,
            value.ScheduleRevision,
            value.Current is null ? null : Map(value.Current),
            value.Upcoming.Select(Map).ToArray());

    private static LiveRadioProgramme Map(WebLiveRadioProgramme value)
        => new(
            value.ScheduleEntryId,
            value.StartsAt,
            value.EndsAt,
            value.PositionMs,
            value.SelectionReason,
            LoopbackLibraryBrowseService.Map(value.Broadcast));

    private sealed record LiveRadioEnvelope(WebLiveRadioSnapshot Station);
}
