using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

public sealed class LoopbackLibraryActionService : ILibraryActionService
{
    private readonly LoopbackServerClient _connection;

    public LoopbackLibraryActionService(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task SetFavouriteAsync(long representativeEpisodeId, bool favourite, CancellationToken cancellationToken = default)
    {
        if (representativeEpisodeId <= 0) throw new ArgumentOutOfRangeException(nameof(representativeEpisodeId));
        var envelope = await _connection.SendJsonAsync<MutationEnvelope>(
            HttpMethod.Post,
            WebApiRoutes.Favourite(representativeEpisodeId),
            new { favourite },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!envelope.Result.Changed) throw new InvalidOperationException(envelope.Result.Message);
    }

    public async Task SetPlayedAsync(long representativeEpisodeId, bool played, CancellationToken cancellationToken = default)
    {
        if (representativeEpisodeId <= 0) throw new ArgumentOutOfRangeException(nameof(representativeEpisodeId));
        var envelope = await _connection.SendJsonAsync<MutationEnvelope>(
            HttpMethod.Post,
            WebApiRoutes.ListeningStatus(representativeEpisodeId),
            new { played },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!envelope.Result.Changed) throw new InvalidOperationException(envelope.Result.Message);
    }
}

public sealed class LoopbackQueueService : IQueueService
{
    private readonly LoopbackServerClient _connection;

    public LoopbackQueueService(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<IReadOnlyList<QueueRecord>> GetAsync(CancellationToken cancellationToken = default)
    {
        var envelope = await _connection.SendJsonAsync<QueueEnvelope>(
            HttpMethod.Get, WebApiRoutes.Queue, cancellationToken: cancellationToken).ConfigureAwait(false);
        return envelope.Queue.Select(Map).ToArray();
    }

    public async Task<long> AddAsync(long broadcastId, bool playNext = false, CancellationToken cancellationToken = default)
    {
        if (broadcastId <= 0) throw new ArgumentOutOfRangeException(nameof(broadcastId));
        var envelope = await _connection.SendJsonAsync<QueueResultEnvelope>(
            HttpMethod.Post,
            WebApiRoutes.QueueAdd,
            new { episodeId = broadcastId, playNext },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var item = envelope.Result.Queue.FirstOrDefault(value => value.Episode.Id == broadcastId);
        return item?.QueueId ?? throw new InvalidOperationException(envelope.Result.Message);
    }

    public async Task RemoveAsync(long queueItemId, CancellationToken cancellationToken = default)
    {
        var envelope = await _connection.SendJsonAsync<QueueResultEnvelope>(
            HttpMethod.Post, WebApiRoutes.QueueRemove(queueItemId), cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!envelope.Result.Changed) throw new InvalidOperationException(envelope.Result.Message);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
        => await _connection.SendJsonAsync<QueueResultEnvelope>(
            HttpMethod.Post, WebApiRoutes.QueueClear, cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task MoveAsync(long queueItemId, int direction, CancellationToken cancellationToken = default)
    {
        if (direction == 0) return;
        var envelope = await _connection.SendJsonAsync<QueueResultEnvelope>(
            HttpMethod.Post,
            WebApiRoutes.QueueMove(queueItemId),
            new { direction = direction < 0 ? -1 : 1 },
            allowConflict: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!envelope.Result.Changed) throw new InvalidOperationException(envelope.Result.Message);
    }

    private static QueueRecord Map(WebQueueItem value)
    {
        DateOnly? airDate = value.Episode.AirDate.HasValue ? DateOnly.FromDateTime(value.Episode.AirDate.Value) : null;
        var addedAt = value.Episode.DateAdded == default
            ? DateTimeOffset.MinValue
            : new DateTimeOffset(value.Episode.DateAdded);
        return new QueueRecord(
            value.QueueId,
            value.Episode.Id,
            value.Position,
            addedAt,
            value.Episode.Show,
            value.Episode.Title,
            value.Episode.Title,
            string.Empty,
            airDate);
    }

    private sealed record QueueEnvelope(IReadOnlyList<WebQueueItem> Queue);
    private sealed record QueueResultEnvelope(WebQueueMutationResult Result);
}

public sealed class LoopbackMomentsService : IMomentsService
{
    private readonly LoopbackServerClient _connection;

    public LoopbackMomentsService(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<IReadOnlyList<MomentRecord>> GetForBroadcastAsync(long broadcastId, CancellationToken cancellationToken = default)
        => (await LoadAsync(cancellationToken).ConfigureAwait(false))
            .Where(value => value.BroadcastId == broadcastId)
            .OrderBy(value => value.PositionMs)
            .ToArray();

    public async Task<IReadOnlyList<MomentRecord>> SearchAsync(string? searchText, int limit = 500, CancellationToken cancellationToken = default)
    {
        IEnumerable<MomentRecord> query = await LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(searchText))
        {
            var search = searchText.Trim();
            query = query.Where(value =>
                Contains(value.Title, search) ||
                Contains(value.Notes, search) ||
                Contains(value.CollectionName, search) ||
                Contains(value.BroadcastTitle, search));
        }
        return query.OrderByDescending(value => value.CreatedAt).Take(Math.Clamp(limit, 1, 5000)).ToArray();
    }

    public async Task<long> AddAsync(
        long broadcastId,
        long positionMs,
        string title,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        if (broadcastId <= 0) throw new ArgumentOutOfRangeException(nameof(broadcastId));
        var envelope = await _connection.SendJsonAsync<MomentResultEnvelope>(
            HttpMethod.Post,
            WebApiRoutes.Moments(broadcastId),
            new WebMomentMutation(Math.Max(0, positionMs), title?.Trim() ?? string.Empty, notes?.Trim() ?? string.Empty),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return envelope.Result.Moment?.Id ?? throw new InvalidOperationException(envelope.Result.Message);
    }

    public async Task UpdateAsync(long momentId, string title, string? notes, CancellationToken cancellationToken = default)
    {
        var envelope = await _connection.SendJsonAsync<MutationEnvelope>(
            HttpMethod.Post,
            WebApiRoutes.MomentUpdate(momentId),
            new WebMomentEditMutation(title?.Trim() ?? string.Empty, notes?.Trim() ?? string.Empty),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!envelope.Result.Changed) throw new InvalidOperationException(envelope.Result.Message);
    }

    public async Task DeleteAsync(long momentId, CancellationToken cancellationToken = default)
    {
        var moment = (await LoadAsync(cancellationToken).ConfigureAwait(false)).FirstOrDefault(value => value.Id == momentId)
            ?? throw new InvalidOperationException("Moment not found.");
        var envelope = await _connection.SendJsonAsync<MutationEnvelope>(
            HttpMethod.Post,
            WebApiRoutes.Moment(moment.BroadcastId, momentId),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (!envelope.Result.Changed) throw new InvalidOperationException(envelope.Result.Message);
    }

    private async Task<IReadOnlyList<MomentRecord>> LoadAsync(CancellationToken cancellationToken)
    {
        var envelope = await _connection.SendJsonAsync<MomentsEnvelope>(
            HttpMethod.Get, WebApiRoutes.MomentsAll, cancellationToken: cancellationToken).ConfigureAwait(false);
        return envelope.Moments.Select(Map).ToArray();
    }

    private static MomentRecord Map(WebMomentSummary value)
        => new(
            value.Id,
            value.EpisodeId,
            value.Show,
            value.EpisodeTitle,
            value.AirDate.HasValue ? DateOnly.FromDateTime(value.AirDate.Value) : null,
            value.PositionMs,
            value.Title,
            value.Notes,
            new DateTimeOffset(value.CreatedAt));

    private static bool Contains(string? value, string search)
        => value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;

    private sealed record MomentsEnvelope(IReadOnlyList<WebMomentSummary> Moments);
    private sealed record MomentResultEnvelope(WebMomentMutationResult Result);
}

public sealed class LoopbackPlaybackLibraryService : ILocalPlaybackLibraryService
{
    private readonly LoopbackServerClient _connection;
    private readonly ServerMediaProxy _mediaProxy;
    private readonly INativeDownloadService? _downloads;
    private readonly string _clientId;

    public LoopbackPlaybackLibraryService(
        LoopbackServerClient connection,
        ServerMediaProxy mediaProxy,
        MachineIdentityService? machineIdentity = null,
        INativeDownloadService? downloads = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _mediaProxy = mediaProxy ?? throw new ArgumentNullException(nameof(mediaProxy));
        _downloads = downloads;
        var identity = (machineIdentity ?? new MachineIdentityService()).LoadOrCreate();
        _clientId = "native-" + identity.MachineId.Replace("-", string.Empty, StringComparison.Ordinal);
    }

    public Task<LocalPlaybackDescriptor> PrepareAsync(long representativeEpisodeId, CancellationToken cancellationToken = default)
        => PrepareAsync(string.Empty, representativeEpisodeId, cancellationToken);

    public async Task<LocalPlaybackDescriptor> PrepareAsync(
        string canonicalKey,
        long representativeEpisodeId,
        CancellationToken cancellationToken = default)
    {
        if (representativeEpisodeId <= 0) throw new ArgumentOutOfRangeException(nameof(representativeEpisodeId));
        if (_downloads is not null)
        {
            var downloaded = await _downloads.TryPrepareAsync(
                canonicalKey,
                representativeEpisodeId,
                cancellationToken).ConfigureAwait(false);
            if (downloaded is not null) return downloaded;
        }

        var summaryTask = _connection.GetJsonOrNullAsync<BroadcastEnvelope>(
            WebApiRoutes.ClientLibraryBroadcast(representativeEpisodeId), cancellationToken);
        var manifestTask = _connection.GetJsonOrNullAsync<WebCanonicalMediaManifest>(
            WebApiRoutes.MediaManifest(representativeEpisodeId), cancellationToken);
        await Task.WhenAll(summaryTask, manifestTask).ConfigureAwait(false);
        var summary = (await summaryTask.ConfigureAwait(false))?.Broadcast
            ?? throw new InvalidOperationException("The selected broadcast no longer exists on Radio Vault Server.");
        var manifest = await manifestTask.ConfigureAwait(false)
            ?? throw new FileNotFoundException("Radio Vault Server could not resolve a complete playable recording for this broadcast.");
        var parts = manifest.Parts.OrderBy(value => value.PartNumber).Select(part =>
        {
            var route = WebApiRoutes.MediaPart(manifest.EpisodeId, part.MediaFileId) +
                "?recording=" + Uri.EscapeDataString(manifest.RecordingKey);
            return new LocalPlaybackSegment(
                part.PartNumber,
                part.PartTotal,
                Math.Max(0, part.LogicalStartMs),
                Math.Max(part.LogicalStartMs, part.LogicalEndMs),
                _mediaProxy.Register(route),
                Math.Max(0, part.LogicalEndMs - part.LogicalStartMs));
        }).ToArray();
        if (parts.Length == 0)
            throw new FileNotFoundException("Radio Vault Server returned an empty playback plan.");
        var duration = Math.Max(summary.DurationMs, Math.Max(manifest.DurationMs, parts.Max(value => value.LogicalEndMs)));
        return new LocalPlaybackDescriptor(
            string.IsNullOrWhiteSpace(manifest.CanonicalKey) ? canonicalKey : manifest.CanonicalKey,
            summary.RepresentativeEpisodeId,
            summary.BroadcastId,
            summary.Title ?? summary.AirDate?.ToString("d MMMM yyyy") ?? "Untitled broadcast",
            summary.CollectionName,
            summary.AirDate,
            summary.ArtworkPath,
            summary.Completed ? 0 : Math.Clamp(summary.PositionMs, 0, Math.Max(0, duration)),
            duration,
            1d,
            summary.Completed,
            summary.Favourite,
            parts);
    }

    public async Task SaveAsync(LocalPlaybackSaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var update = new WebOfflineProgressUpdate(
            _clientId,
            request.RepresentativeEpisodeId,
            Math.Max(0, request.PositionMs),
            Math.Max(0, request.DurationMs),
            request.Completed,
            Math.Clamp(request.PlaybackSpeed, 0.5d, 3d),
            DateTimeOffset.UtcNow,
            AllowRewind: request.ExpectedPlaybackGeneration > 0,
            ExpectedGeneration: request.ExpectedPlaybackGeneration,
            ExplicitSeek: request.ExplicitSeek || request.AllowCompletionReset,
            IncrementPlayCount: request.IncrementPlayCount);
        ProgressEnvelope envelope;
        try
        {
            envelope = await _connection.SendJsonAsync<ProgressEnvelope>(
                HttpMethod.Post,
                WebApiRoutes.OfflineProgress(request.RepresentativeEpisodeId),
                update,
                allowConflict: true,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await PersistDownloadedProgressAfterServerFailureAsync(request, cancellationToken)
                .ConfigureAwait(false);
            throw;
        }
        if (envelope.Result.Conflict) throw new InvalidOperationException(envelope.Result.Message);
        if (_downloads is not null)
            await _downloads.UpdatePlaybackStateAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public Task FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private sealed record ProgressEnvelope(WebOfflineProgressResult Result);
    private sealed record BroadcastEnvelope(WebClientLibraryBroadcastSummary Broadcast);

    private async Task PersistDownloadedProgressAfterServerFailureAsync(
        LocalPlaybackSaveRequest request,
        CancellationToken cancellationToken)
    {
        if (_downloads is null || cancellationToken.IsCancellationRequested) return;
        try
        {
            await _downloads.UpdatePlaybackStateAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (exception is not OperationCanceledException)
                DiagnosticLog.Write(
                    "Native downloads",
                    "Downloaded playback progress could not be saved after the server request failed.",
                    exception);
        }
    }
}

file sealed record MutationEnvelope(WebMutationResult Result);
