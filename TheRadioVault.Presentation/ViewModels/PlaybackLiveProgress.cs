using TheRadioVault.Services.Models;

namespace TheRadioVault.Presentation.ViewModels;

/// <summary>
/// Presentation-safe projection of the authoritative playback position. It can
/// represent either this process's decoder or the shared session currently
/// owned by another phone/desktop endpoint.
/// </summary>
public sealed record PlaybackLiveProgress(
    long RepresentativeEpisodeId,
    string CanonicalKey,
    long PositionMs,
    long DurationMs,
    bool Completed,
    bool IsPlaying,
    bool IsOwnedByCurrentDevice,
    DateTimeOffset ObservedAt)
{
    public int ProgressPercent => Completed
        ? 100
        : DurationMs <= 0
            ? 0
            : Math.Clamp((int)Math.Round(PositionMs * 100d / DurationMs), 0, 99);

    public bool Matches(LibraryBroadcastSummary source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (RepresentativeEpisodeId > 0 && source.RepresentativeEpisodeId == RepresentativeEpisodeId)
            return true;
        return !string.IsNullOrWhiteSpace(CanonicalKey)
               && string.Equals(source.CanonicalKey, CanonicalKey, StringComparison.OrdinalIgnoreCase);
    }
}
