using System.Collections.Concurrent;
using TheRadioVault.Transcription.Contracts;
using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Services;

public sealed class TranscriptionBatchCoordinator : ITranscriptionBatchCoordinator
{
    private readonly ITranscriptionBatchRepository _batches;
    private readonly ITranscriptRepository _transcripts;
    private readonly ITranscriptionCoordinator _transcription;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _processors = new();
    private readonly ConcurrentDictionary<Guid, Guid> _activeJobs = new();
    private bool _disposed;

    public TranscriptionBatchCoordinator(
        ITranscriptionBatchRepository batches,
        ITranscriptRepository transcripts,
        ITranscriptionCoordinator transcription)
    {
        _batches = batches ?? throw new ArgumentNullException(nameof(batches));
        _transcripts = transcripts ?? throw new ArgumentNullException(nameof(transcripts));
        _transcription = transcription ?? throw new ArgumentNullException(nameof(transcription));
    }

    public async Task<TranscriptionBatchRecord> CreateAndStartAsync(
        TranscriptionBatchCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TranscriptionBatchCoordinator));
        var created = await _batches.CreateAsync(request, cancellationToken).ConfigureAwait(false);
        if (created.PendingCount == 0)
        {
            await _batches.SetBatchStateAsync(created.BatchId, TranscriptionBatchState.Completed, cancellationToken).ConfigureAwait(false);
            return await _batches.GetAsync(created.BatchId, cancellationToken).ConfigureAwait(false) ?? created;
        }
        await _batches.SetBatchStateAsync(created.BatchId, TranscriptionBatchState.Running, cancellationToken).ConfigureAwait(false);
        StartProcessor(created.BatchId);
        return await _batches.GetAsync(created.BatchId, cancellationToken).ConfigureAwait(false) ?? created;
    }

    public Task<IReadOnlyList<TranscriptionBatchRecord>> GetBatchesAsync(int limit = 50, CancellationToken cancellationToken = default)
        => _batches.GetBatchesAsync(limit, cancellationToken);

    public Task<IReadOnlyList<TranscriptionBatchItemRecord>> GetItemsAsync(Guid batchId, CancellationToken cancellationToken = default)
        => _batches.GetItemsAsync(batchId, cancellationToken);

    public async Task PauseAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        var batch = await RequireBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (!batch.CanPause) return;
        await _batches.SetBatchStateAsync(batchId, TranscriptionBatchState.Paused, cancellationToken).ConfigureAwait(false);
        if (_activeJobs.TryGetValue(batchId, out var jobId))
            await _transcription.PauseAsync(jobId, cancellationToken).ConfigureAwait(false);
    }

    public async Task ResumeAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        var batch = await RequireBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (!batch.CanResume) return;
        await _batches.SetBatchStateAsync(batchId, TranscriptionBatchState.Running, cancellationToken).ConfigureAwait(false);
        if (_activeJobs.TryGetValue(batchId, out var jobId))
            await _transcription.ResumeAsync(jobId, cancellationToken).ConfigureAwait(false);
        StartProcessor(batchId);
    }

    public async Task CancelAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        var batch = await RequireBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (!batch.CanCancel) return;
        await _batches.SetBatchStateAsync(batchId, TranscriptionBatchState.Cancelled, cancellationToken).ConfigureAwait(false);
        await _batches.CancelPendingItemsAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (_activeJobs.TryGetValue(batchId, out var jobId)) _transcription.Cancel(jobId);
    }

    public async Task RetryFailedAsync(Guid batchId, CancellationToken cancellationToken = default)
    {
        var batch = await RequireBatchAsync(batchId, cancellationToken).ConfigureAwait(false);
        if (!batch.CanRetryFailed) return;
        await _batches.ResetFailedItemsAsync(batchId, cancellationToken).ConfigureAwait(false);
        await _batches.SetBatchStateAsync(batchId, TranscriptionBatchState.Running, cancellationToken).ConfigureAwait(false);
        StartProcessor(batchId);
    }

    public Task<bool> MoveItemAsync(Guid batchId, long itemId, int direction, CancellationToken cancellationToken = default)
        => _batches.MoveItemAsync(batchId, itemId, direction, cancellationToken);

    private void StartProcessor(Guid batchId)
    {
        if (_disposed) return;
        var cancellation = new CancellationTokenSource();
        if (!_processors.TryAdd(batchId, cancellation))
        {
            cancellation.Dispose();
            return;
        }
        _ = Task.Run(async () =>
        {
            try { await ProcessAsync(batchId, cancellation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally
            {
                _activeJobs.TryRemove(batchId, out _);
                if (_processors.TryRemove(batchId, out var removed)) removed.Dispose();
            }
        }, cancellation.Token);
    }

    private async Task ProcessAsync(Guid batchId, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await _batches.GetAsync(batchId, cancellationToken).ConfigureAwait(false);
            if (batch is null || batch.State == TranscriptionBatchState.Cancelled) return;
            if (batch.State == TranscriptionBatchState.Paused)
            {
                await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                continue;
            }
            if (batch.State is not TranscriptionBatchState.Running and not TranscriptionBatchState.Queued) return;

            var items = await _batches.GetItemsAsync(batchId, cancellationToken).ConfigureAwait(false);
            var item = items.FirstOrDefault(x => x.State == TranscriptionBatchItemState.Pending);
            if (item is null)
            {
                if (items.Any(x => x.State == TranscriptionBatchItemState.Running))
                {
                    await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                    continue;
                }
                var finalState = items.Any(x => x.State == TranscriptionBatchItemState.Failed)
                    ? TranscriptionBatchState.CompletedWithErrors
                    : TranscriptionBatchState.Completed;
                await _batches.SetBatchStateAsync(batchId, finalState, CancellationToken.None).ConfigureAwait(false);
                return;
            }

            var existing = await _transcripts.GetSummaryForEpisodeAsync(item.EpisodeId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                await _batches.SetItemStateAsync(item.Id, TranscriptionBatchItemState.Skipped, error: "Transcript already exists", cancellationToken: cancellationToken).ConfigureAwait(false);
                continue;
            }

            try
            {
                var options = new TranscriptionJobOptions(
                    batch.Language,
                    batch.ModelId,
                    EnableSpeakerDiarization: batch.EnableSpeakerDiarization,
                    UseVoiceActivityDetection: batch.UseVoiceActivityDetection);
                var jobId = await _transcription.QueueAsync(item.EpisodeId, options, cancellationToken).ConfigureAwait(false);
                _activeJobs[batchId] = jobId;
                await _batches.SetItemStateAsync(item.Id, TranscriptionBatchItemState.Running, jobId, cancellationToken: cancellationToken).ConfigureAwait(false);
                await WaitForJobAsync(batchId, item.Id, jobId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                await _batches.SetItemStateAsync(item.Id, TranscriptionBatchItemState.Failed, error: FailureSummary(exception), cancellationToken: CancellationToken.None).ConfigureAwait(false);
            }
            finally { _activeJobs.TryRemove(batchId, out _); }
        }
    }

    private async Task WaitForJobAsync(Guid batchId, long itemId, Guid jobId, CancellationToken cancellationToken)
    {
        var pauseApplied = false;
        while (!cancellationToken.IsCancellationRequested)
        {
            var batch = await _batches.GetAsync(batchId, cancellationToken).ConfigureAwait(false);
            if (batch is null) return;
            if (batch.State == TranscriptionBatchState.Cancelled)
            {
                _transcription.Cancel(jobId);
            }
            else if (batch.State == TranscriptionBatchState.Paused && !pauseApplied)
            {
                pauseApplied = await _transcription.PauseAsync(jobId, cancellationToken).ConfigureAwait(false);
            }
            else if (batch.State == TranscriptionBatchState.Running && pauseApplied)
            {
                await _transcription.ResumeAsync(jobId, cancellationToken).ConfigureAwait(false);
                pauseApplied = false;
            }

            var job = await _transcripts.GetJobAsync(jobId, cancellationToken).ConfigureAwait(false);
            if (job is null)
            {
                await _batches.SetItemStateAsync(itemId, TranscriptionBatchItemState.Failed, jobId, "The transcription job disappeared.", CancellationToken.None).ConfigureAwait(false);
                return;
            }
            switch (job.State)
            {
                case TranscriptionJobState.Completed:
                    await _batches.SetItemStateAsync(itemId, TranscriptionBatchItemState.Completed, jobId, cancellationToken: CancellationToken.None).ConfigureAwait(false);
                    return;
                case TranscriptionJobState.Failed:
                case TranscriptionJobState.Interrupted:
                    await _batches.SetItemStateAsync(itemId, TranscriptionBatchItemState.Failed, jobId, FailureSummary(job), CancellationToken.None).ConfigureAwait(false);
                    return;
                case TranscriptionJobState.Cancelled:
                    var itemState = batch.State == TranscriptionBatchState.Cancelled
                        ? TranscriptionBatchItemState.Cancelled
                        : TranscriptionBatchItemState.Failed;
                    await _batches.SetItemStateAsync(itemId, itemState, jobId, job.Message, CancellationToken.None).ConfigureAwait(false);
                    return;
            }
            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<TranscriptionBatchRecord> RequireBatchAsync(Guid batchId, CancellationToken cancellationToken)
        => await _batches.GetAsync(batchId, cancellationToken).ConfigureAwait(false)
           ?? throw new InvalidOperationException("The selected transcription batch no longer exists.");

    private static string FailureSummary(Exception exception)
    {
        var message = exception.Message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Batch item failed";
        return message.Length <= 180 ? message : message[..177] + "…";
    }

    private static string FailureSummary(TranscriptionJobRecord job)
    {
        var source = string.IsNullOrWhiteSpace(job.Error) ? job.Message : job.Error;
        var message = source.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "Transcription failed";
        return message.Length <= 180 ? message : message[..177] + "…";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var cancellation in _processors.Values) cancellation.Cancel();
    }
}
