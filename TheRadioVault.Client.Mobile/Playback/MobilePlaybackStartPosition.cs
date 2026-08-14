using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile.Playback;

/// <summary>
/// Keeps an explicit timeline or transcript start position authoritative when
/// the server refreshes the broadcast summary during decoder preparation.
/// Normal play requests continue to use the latest shared Library progress.
/// </summary>
internal static class MobilePlaybackStartPosition
{
    public static WebClientLibraryBroadcastSummary Apply(
        WebClientLibraryBroadcastSummary refreshed,
        long? requestedPositionMs)
    {
        ArgumentNullException.ThrowIfNull(refreshed);
        if (requestedPositionMs is not { } requested) return refreshed;
        var position = Math.Max(0, requested);
        return refreshed with
        {
            PositionMs = position,
            Completed = false,
            InProgress = position > 0
        };
    }
}
