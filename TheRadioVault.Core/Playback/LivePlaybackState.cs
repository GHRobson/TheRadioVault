namespace TheRadioVault.Core.Playback;

public sealed record LivePlaybackSnapshot(
    long? EpisodeId,
    string Show,
    string Title,
    long PositionMs,
    long DurationMs,
    string Status,
    bool IsPlaying,
    DateTimeOffset UpdatedAt)
{
    public static LivePlaybackSnapshot Idle { get; } = new(null, string.Empty, string.Empty, 0, 0, "Idle", false, DateTimeOffset.MinValue);
}

public interface ILivePlaybackStateStore
{
    LivePlaybackSnapshot Current { get; }
    void Update(LivePlaybackSnapshot snapshot);
}

public sealed class LivePlaybackStateStore : ILivePlaybackStateStore
{
    private LivePlaybackSnapshot _current = LivePlaybackSnapshot.Idle;
    public LivePlaybackSnapshot Current => Volatile.Read(ref _current);

    public void Update(LivePlaybackSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
    }
}
