using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile.Synchronization;

/// <summary>
/// Owns the durable offline-mutation queue and its ordered, single-flight
/// reconciliation with the authoritative server.
/// </summary>
internal sealed class MobileOfflineMutationSynchronizationCoordinator : IDisposable
{
    private readonly MobileOfflineMutationStore _store;
    private readonly IMobileOfflineMutationTransport _transport;
    private readonly Func<WebClientLibraryBroadcastSummary, CancellationToken, Task> _reconcileBroadcast;
    private readonly Action<WebClientLibraryBroadcastSummary> _applyAlreadyCanonicalBroadcast;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public MobileOfflineMutationSynchronizationCoordinator(
        MobileOfflineMutationStore store,
        IMobileOfflineMutationTransport transport,
        Func<WebClientLibraryBroadcastSummary, CancellationToken, Task> reconcileBroadcast,
        Action<WebClientLibraryBroadcastSummary> applyAlreadyCanonicalBroadcast)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _reconcileBroadcast = reconcileBroadcast ?? throw new ArgumentNullException(nameof(reconcileBroadcast));
        _applyAlreadyCanonicalBroadcast = applyAlreadyCanonicalBroadcast ??
            throw new ArgumentNullException(nameof(applyAlreadyCanonicalBroadcast));
    }

    public Task<MobileSyncDiagnostics> GetDiagnosticsAsync() => _store.GetDiagnosticsAsync();

    public Task<IReadOnlyList<MobileOfflineMutation>> GetPendingAsync(string serverInstanceId)
        => _store.GetPendingAsync(serverInstanceId);

    public Task EnqueueFavouriteAsync(string serverInstanceId, long episodeId, bool favourite)
        => _store.EnqueueFavouriteAsync(serverInstanceId, episodeId, favourite);

    public Task EnqueueListeningStatusAsync(string serverInstanceId, long episodeId, bool played)
        => _store.EnqueueListeningStatusAsync(serverInstanceId, episodeId, played);

    public Task EnqueueMomentAsync(
        string serverInstanceId,
        long episodeId,
        long positionMs,
        string title,
        string notes,
        string mutationId)
        => _store.EnqueueMomentAsync(
            serverInstanceId,
            episodeId,
            positionMs,
            title,
            notes,
            mutationId);

    public Task ClearAsync() => _store.ClearAsync();

    public async Task<MobileOfflineMutationSynchronizationResult> FlushAsync(
        string serverInstanceId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pending = await _store.GetPendingAsync(serverInstanceId).ConfigureAwait(false);
            var synchronized = 0;
            MobileOfflineMutation? failed = null;
            foreach (var mutation in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    await ApplyMutationAsync(mutation, cancellationToken).ConfigureAwait(false);
                    await _store.MarkSucceededAsync(mutation.Id).ConfigureAwait(false);
                    synchronized++;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    if (await MutationAlreadyAppliedAsync(mutation, cancellationToken).ConfigureAwait(false))
                    {
                        await _store.MarkSucceededAsync(mutation.Id).ConfigureAwait(false);
                        synchronized++;
                        continue;
                    }

                    await _store.MarkFailedAsync(mutation.Id, exception.Message).ConfigureAwait(false);
                    failed = mutation;
                    break;
                }
            }

            return new MobileOfflineMutationSynchronizationResult(
                synchronized,
                failed,
                await _store.GetDiagnosticsAsync().ConfigureAwait(false));
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyMutationAsync(
        MobileOfflineMutation mutation,
        CancellationToken cancellationToken)
    {
        switch (mutation.Kind)
        {
            case MobileOfflineMutationKind.Favourite:
                await _transport.SetFavouriteAsync(
                    mutation.EpisodeId,
                    mutation.BooleanValue == true,
                    cancellationToken).ConfigureAwait(false);
                await ReconcileBroadcastAsync(mutation.EpisodeId, cancellationToken).ConfigureAwait(false);
                break;
            case MobileOfflineMutationKind.ListeningStatus:
                await _transport.SetListeningStatusAsync(
                    mutation.EpisodeId,
                    mutation.BooleanValue == true,
                    cancellationToken).ConfigureAwait(false);
                await ReconcileBroadcastAsync(mutation.EpisodeId, cancellationToken).ConfigureAwait(false);
                break;
            case MobileOfflineMutationKind.Moment:
                var result = await _transport.AddMomentAsync(
                    mutation.EpisodeId,
                    new WebMomentMutation(
                        mutation.PositionMs,
                        mutation.Title,
                        mutation.Notes,
                        mutation.MutationId),
                    cancellationToken).ConfigureAwait(false);
                if (!result.Changed && !result.Duplicate)
                    throw new InvalidOperationException(result.Message);
                break;
            default:
                throw new InvalidOperationException($"Unknown offline mutation kind: {mutation.Kind}.");
        }
    }

    private async Task ReconcileBroadcastAsync(long episodeId, CancellationToken cancellationToken)
    {
        var summary = await _transport
            .GetBroadcastSummaryAsync(episodeId, cancellationToken)
            .ConfigureAwait(false);
        await _reconcileBroadcast(summary, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> MutationAlreadyAppliedAsync(
        MobileOfflineMutation mutation,
        CancellationToken cancellationToken)
    {
        if (mutation.Kind == MobileOfflineMutationKind.Moment) return false;
        try
        {
            var summary = await _transport
                .GetBroadcastSummaryAsync(mutation.EpisodeId, cancellationToken)
                .ConfigureAwait(false);
            var applied = mutation.Kind switch
            {
                MobileOfflineMutationKind.Favourite => summary.Favourite == (mutation.BooleanValue == true),
                MobileOfflineMutationKind.ListeningStatus =>
                    summary.Completed == (mutation.BooleanValue == true) &&
                    (mutation.BooleanValue == true || summary.PositionMs == 0),
                _ => false
            };
            if (applied) _applyAlreadyCanonicalBroadcast(summary);
            return applied;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }
}

internal sealed record MobileOfflineMutationSynchronizationResult(
    int SynchronizedCount,
    MobileOfflineMutation? FailedMutation,
    MobileSyncDiagnostics Diagnostics);

internal interface IMobileOfflineMutationTransport
{
    Task<WebMutationResult> SetFavouriteAsync(
        long episodeId,
        bool favourite,
        CancellationToken cancellationToken = default);

    Task<WebMutationResult> SetListeningStatusAsync(
        long episodeId,
        bool played,
        CancellationToken cancellationToken = default);

    Task<WebMomentMutationResult> AddMomentAsync(
        long episodeId,
        WebMomentMutation mutation,
        CancellationToken cancellationToken = default);

    Task<WebClientLibraryBroadcastSummary> GetBroadcastSummaryAsync(
        long episodeId,
        CancellationToken cancellationToken = default);
}

internal sealed class MobileOfflineMutationTransport(
    MobileServerClient server) : IMobileOfflineMutationTransport
{
    private readonly MobileServerClient _server = server ?? throw new ArgumentNullException(nameof(server));

    public Task<WebMutationResult> SetFavouriteAsync(
        long episodeId,
        bool favourite,
        CancellationToken cancellationToken = default)
        => _server.SetFavouriteAsync(episodeId, favourite, cancellationToken);

    public Task<WebMutationResult> SetListeningStatusAsync(
        long episodeId,
        bool played,
        CancellationToken cancellationToken = default)
        => _server.SetListeningStatusAsync(episodeId, played, cancellationToken);

    public Task<WebMomentMutationResult> AddMomentAsync(
        long episodeId,
        WebMomentMutation mutation,
        CancellationToken cancellationToken = default)
        => _server.AddMomentAsync(episodeId, mutation, cancellationToken);

    public Task<WebClientLibraryBroadcastSummary> GetBroadcastSummaryAsync(
        long episodeId,
        CancellationToken cancellationToken = default)
        => _server.GetBroadcastSummaryAsync(episodeId, cancellationToken);
}
