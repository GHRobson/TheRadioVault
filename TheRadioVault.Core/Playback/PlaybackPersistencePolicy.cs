namespace TheRadioVault.Core.Playback;

/// <summary>
/// Guards persisted progress against transient zero positions emitted while a
/// media source is opening, closing, or transferring between devices. A reset
/// to the beginning is accepted only through an explicit user reset operation.
/// </summary>
public static class PlaybackPersistencePolicy
{
    public static long ResolvePosition(long requestedPositionMs, long existingPositionMs, bool allowPositionReset)
    {
        var requested = Math.Max(0, requestedPositionMs);
        var existing = Math.Max(0, existingPositionMs);
        return !allowPositionReset && requested == 0 && existing > 0
            ? existing
            : requested;
    }
}
