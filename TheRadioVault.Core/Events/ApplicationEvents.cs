using System.Collections.Concurrent;

namespace TheRadioVault.Core.Events;

public interface IApplicationEvent
{
    DateTimeOffset OccurredAt { get; }
}

public interface IApplicationEventBus
{
    void Publish<TEvent>(TEvent applicationEvent) where TEvent : IApplicationEvent;
    IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IApplicationEvent;
}

/// <summary>
/// Small in-process typed event bus used to keep platform clients and background
/// services decoupled. Handlers are invoked synchronously in subscription order;
/// subscribers that need UI affinity must marshal to their own dispatcher.
/// </summary>
public sealed class ApplicationEventBus : IApplicationEventBus
{
    private readonly object _gate = new();
    private readonly Dictionary<Type, List<Subscription>> _subscriptions = new();

    public void Publish<TEvent>(TEvent applicationEvent) where TEvent : IApplicationEvent
    {
        ArgumentNullException.ThrowIfNull(applicationEvent);
        Subscription[] subscribers;
        lock (_gate)
        {
            subscribers = _subscriptions.TryGetValue(typeof(TEvent), out var list)
                ? list.ToArray()
                : Array.Empty<Subscription>();
        }

        foreach (var subscription in subscribers)
        {
            if (subscription.IsDisposed) continue;
            try { subscription.Invoke(applicationEvent); }
            catch { /* An observer must not break the operation that published the event. */ }
        }
    }

    public IDisposable Subscribe<TEvent>(Action<TEvent> handler) where TEvent : IApplicationEvent
    {
        ArgumentNullException.ThrowIfNull(handler);
        var subscription = new Subscription(this, typeof(TEvent), value => handler((TEvent)value));
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(typeof(TEvent), out var list))
            {
                list = new List<Subscription>();
                _subscriptions[typeof(TEvent)] = list;
            }
            list.Add(subscription);
        }
        return subscription;
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(subscription.EventType, out var list)) return;
            list.Remove(subscription);
            if (list.Count == 0) _subscriptions.Remove(subscription.EventType);
        }
    }

    private sealed class Subscription : IDisposable
    {
        private readonly ApplicationEventBus _owner;
        private readonly Action<object> _handler;
        private int _disposed;

        public Subscription(ApplicationEventBus owner, Type eventType, Action<object> handler)
        {
            _owner = owner;
            EventType = eventType;
            _handler = handler;
        }

        public Type EventType { get; }
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public void Invoke(object value) => _handler(value);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _owner.Remove(this);
        }
    }
}

public sealed record LibraryScanCompletedEvent(
    int FilesFound,
    int Added,
    int Updated,
    int Unchanged,
    int Errors,
    int ResearchCandidatesFound,
    DateTimeOffset OccurredAt) : IApplicationEvent;

public sealed record ResearchAuditCompletedEvent(
    int FindingCount,
    int AffectedBroadcasts,
    int SafeRepairCount,
    DateTimeOffset OccurredAt) : IApplicationEvent;

public sealed record ResearchUpdatedEvent(
    long? ResearchBroadcastId,
    long? EpisodeId,
    string Reason,
    DateTimeOffset OccurredAt) : IApplicationEvent;

public sealed record MetadataChangedEvent(
    long? EpisodeId,
    string Reason,
    DateTimeOffset OccurredAt) : IApplicationEvent;

public sealed record FavouritesChangedEvent(
    IReadOnlyList<long> EpisodeIds,
    bool Favourite,
    DateTimeOffset OccurredAt) : IApplicationEvent;

public sealed record ListeningStatusChangedEvent(
    IReadOnlyList<long> EpisodeIds,
    string Status,
    DateTimeOffset OccurredAt) : IApplicationEvent;


public sealed record QueueChangedEvent(
    IReadOnlyList<long> EpisodeIds,
    string Reason,
    DateTimeOffset OccurredAt) : IApplicationEvent;


public sealed record PlaybackPreferencesChangedEvent(
    int SkipBackSeconds,
    int SkipForwardSeconds,
    int CompletionThresholdSeconds,
    DateTimeOffset OccurredAt) : IApplicationEvent;

public sealed record PlaybackChangedEvent(
    long? EpisodeId,
    long PositionMs,
    long DurationMs,
    bool IsPlaying,
    DateTimeOffset OccurredAt) : IApplicationEvent;

/// <summary>
/// Announces an intentional change of the device producing audible playback.
/// Unlike PlaybackChangedEvent, this is emitted only when a device actually
/// takes ownership, so ordinary progress ticks cannot cause ownership churn.
/// </summary>
public sealed record PlaybackOwnershipChangedEvent(
    long? EpisodeId,
    long PositionMs,
    long DurationMs,
    double Speed,
    bool IsPlaying,
    string Device,
    DateTimeOffset OccurredAt) : IApplicationEvent;

public sealed record RemotePlaybackChangedEvent(
    long? EpisodeId,
    string Show,
    string Title,
    long PositionMs,
    long DurationMs,
    double Speed,
    bool IsPlaying,
    string Device,
    DateTimeOffset OccurredAt) : IApplicationEvent;
