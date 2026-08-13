using System.Text.Json;
using TheRadioVault.Client.Mobile.Models;

namespace TheRadioVault.Client.Mobile;

internal sealed class MobileOfflineMutationStore
{
    private readonly string _indexPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MobileOfflineMutationIndex _index = new();
    private bool _loaded;

    public MobileOfflineMutationStore(string rootDirectory)
    {
        Directory.CreateDirectory(rootDirectory);
        _indexPath = Path.Combine(rootDirectory, "pending-changes.json");
    }

    public async Task<MobileSyncDiagnostics> GetDiagnosticsAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync().ConfigureAwait(false);
            return new MobileSyncDiagnostics(
                _index.Pending.Count, _index.LastAttemptAt,
                _index.LastSuccessfulSyncAt, _index.LastError);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<MobileOfflineMutation>> GetPendingAsync(string serverInstanceId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync().ConfigureAwait(false);
            return _index.Pending
                .Where(value => string.Equals(value.ServerInstanceId, serverInstanceId, StringComparison.Ordinal))
                .OrderBy(value => value.CapturedAt)
                .ToArray();
        }
        finally { _gate.Release(); }
    }

    public Task EnqueueFavouriteAsync(string serverInstanceId, long episodeId, bool favourite)
    {
        var id = Guid.NewGuid();
        return EnqueueReplacingAsync(new MobileOfflineMutation(
            id, MobileOfflineMutationKind.Favourite, serverInstanceId, episodeId,
            DateTimeOffset.UtcNow, BooleanValue: favourite, MutationId: id.ToString("N")));
    }

    public Task EnqueueListeningStatusAsync(string serverInstanceId, long episodeId, bool played)
    {
        var id = Guid.NewGuid();
        return EnqueueReplacingAsync(new MobileOfflineMutation(
            id, MobileOfflineMutationKind.ListeningStatus, serverInstanceId, episodeId,
            DateTimeOffset.UtcNow, BooleanValue: played, MutationId: id.ToString("N")));
    }

    public async Task EnqueueMomentAsync(
        string serverInstanceId, long episodeId, long positionMs,
        string title, string notes, string mutationId)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync().ConfigureAwait(false);
            _index.Pending.Add(new MobileOfflineMutation(
                Guid.NewGuid(), MobileOfflineMutationKind.Moment, serverInstanceId, episodeId,
                DateTimeOffset.UtcNow, PositionMs: Math.Max(0, positionMs), Title: title,
                Notes: notes, MutationId: mutationId));
            await SaveUnsafeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkSucceededAsync(Guid id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync().ConfigureAwait(false);
            _index.Pending.RemoveAll(value => value.Id == id);
            _index = new MobileOfflineMutationIndex
            {
                Pending = _index.Pending,
                LastAttemptAt = DateTimeOffset.UtcNow,
                LastSuccessfulSyncAt = DateTimeOffset.UtcNow,
                LastError = string.Empty
            };
            await SaveUnsafeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkFailedAsync(Guid id, string error)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync().ConfigureAwait(false);
            var itemIndex = _index.Pending.FindIndex(value => value.Id == id);
            if (itemIndex >= 0)
                _index.Pending[itemIndex] = _index.Pending[itemIndex] with
                {
                    Attempts = _index.Pending[itemIndex].Attempts + 1,
                    LastError = error
                };
            _index = new MobileOfflineMutationIndex
            {
                Pending = _index.Pending,
                LastAttemptAt = DateTimeOffset.UtcNow,
                LastSuccessfulSyncAt = _index.LastSuccessfulSyncAt,
                LastError = error
            };
            await SaveUnsafeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _index = new MobileOfflineMutationIndex();
            _loaded = true;
            await SaveUnsafeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task EnqueueReplacingAsync(MobileOfflineMutation mutation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await EnsureLoadedUnsafeAsync().ConfigureAwait(false);
            _index.Pending.RemoveAll(value =>
                value.Kind == mutation.Kind && value.EpisodeId == mutation.EpisodeId &&
                string.Equals(value.ServerInstanceId, mutation.ServerInstanceId, StringComparison.Ordinal));
            _index.Pending.Add(mutation);
            await SaveUnsafeAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task EnsureLoadedUnsafeAsync()
    {
        if (_loaded) return;
        _loaded = true;
        if (!File.Exists(_indexPath)) return;
        try
        {
            await using var stream = File.OpenRead(_indexPath);
            _index = await JsonSerializer.DeserializeAsync(
                stream, MobileJsonContext.Default.MobileOfflineMutationIndex).ConfigureAwait(false)
                ?? new MobileOfflineMutationIndex();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            System.Diagnostics.Trace.WriteLine($"[iOS pending changes] Could not load queue: {exception}");
            _index = new MobileOfflineMutationIndex { LastError = exception.Message };
        }
    }

    private async Task SaveUnsafeAsync()
    {
        var temporary = _indexPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream, _index, MobileJsonContext.Default.MobileOfflineMutationIndex).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _indexPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
