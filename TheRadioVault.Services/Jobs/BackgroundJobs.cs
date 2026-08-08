using System.Collections.Concurrent;
using TheRadioVault.Core.Events;

namespace TheRadioVault.Services.Jobs;

public enum BackgroundJobState { Queued, Running, Completed, Failed, Cancelled }
public enum BackgroundJobCategory { General, LibraryScan, ResearchImport, ResearchAudit, ArchiveSync, Fingerprinting, ArchiveComparison, LibraryTruth, Transcription }

public sealed record BackgroundJobProgress(
    Guid JobId,
    string Name,
    BackgroundJobCategory Category,
    BackgroundJobState State,
    double? Percent,
    string? Message,
    DateTimeOffset QueuedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    bool CanCancel,
    Exception? Error = null);

public sealed class BackgroundJobContext
{
    private readonly Action<double?, string?> _report;

    internal BackgroundJobContext(Action<double?, string?> report) => _report = report;

    public void Report(double percent, string? message = null)
        => _report(Math.Clamp(percent, 0, 100), message);

    public void ReportIndeterminate(string? message = null)
        => _report(null, message);
}

public sealed record BackgroundJobRequest(
    string Name,
    BackgroundJobCategory Category,
    Func<BackgroundJobContext, CancellationToken, Task> Work);

public sealed record BackgroundJobChangedEvent(BackgroundJobProgress Job, DateTimeOffset OccurredAt) : IApplicationEvent;

public interface IBackgroundJobQueue
{
    event EventHandler<BackgroundJobProgress>? ProgressChanged;
    Guid Enqueue(string name, Func<IProgress<double>, CancellationToken, Task> work);
    Guid Enqueue(BackgroundJobRequest request);
    Task RunAsync(string name, Func<IProgress<double>, CancellationToken, Task> work, CancellationToken cancellationToken = default);
    Task RunAsync(BackgroundJobRequest request, CancellationToken cancellationToken = default);
    bool Cancel(Guid jobId);
    BackgroundJobProgress? GetJob(Guid jobId);
    IReadOnlyList<BackgroundJobProgress> GetJobs();
    int PruneCompleted(int keepLatest = 50);
}

/// <summary>
/// Shared bounded-concurrency job runner. Jobs retain a compact history for
/// diagnostics and publish typed state changes for desktop and web clients.
/// </summary>
public sealed class BackgroundJobQueue : IBackgroundJobQueue, IDisposable
{
    private readonly ConcurrentDictionary<Guid, JobRegistration> _jobs = new();
    private readonly SemaphoreSlim _concurrency;
    private readonly IApplicationEventBus? _events;
    private readonly int _historyLimit;
    private bool _disposed;
    private int _activeExecutions;
    private int _infrastructureDisposed;

    public BackgroundJobQueue(int maxConcurrency = 2, IApplicationEventBus? events = null, int historyLimit = 100)
    {
        _concurrency = new SemaphoreSlim(Math.Max(1, maxConcurrency));
        _events = events;
        _historyLimit = Math.Max(10, historyLimit);
    }

    public event EventHandler<BackgroundJobProgress>? ProgressChanged;

    public Guid Enqueue(string name, Func<IProgress<double>, CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        return Enqueue(new BackgroundJobRequest(name, BackgroundJobCategory.General, async (context, token) =>
        {
            var adapter = new Progress<double>(value => context.Report(value));
            await work(adapter, token).ConfigureAwait(false);
        }));
    }

    public Guid Enqueue(BackgroundJobRequest request)
    {
        Validate(request);
        var registration = CreateRegistration(request);
        _ = ExecuteDetachedAsync(registration, request.Work);
        return registration.Id;
    }

    public Task RunAsync(string name, Func<IProgress<double>, CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync(new BackgroundJobRequest(name, BackgroundJobCategory.General, async (context, token) =>
        {
            var adapter = new Progress<double>(value => context.Report(value));
            await work(adapter, token).ConfigureAwait(false);
        }), cancellationToken);
    }

    public async Task RunAsync(BackgroundJobRequest request, CancellationToken cancellationToken = default)
    {
        Validate(request);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var registration = CreateRegistration(request, linked);
        await ExecuteAsync(registration, request.Work, rethrow: true).ConfigureAwait(false);
    }

    public bool Cancel(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job) || !job.LastProgress.CanCancel) return false;
        return job.RequestCancellation();
    }

    public BackgroundJobProgress? GetJob(Guid jobId)
        => _jobs.TryGetValue(jobId, out var job) ? job.LastProgress : null;

    public IReadOnlyList<BackgroundJobProgress> GetJobs()
        => _jobs.Values
            .Select(job => job.LastProgress)
            .OrderByDescending(job => job.QueuedAt)
            .ThenByDescending(job => job.JobId)
            .ToList();

    public int PruneCompleted(int keepLatest = 50)
    {
        keepLatest = Math.Max(0, keepLatest);
        var removable = GetJobs()
            .Where(x => x.State is BackgroundJobState.Completed or BackgroundJobState.Failed or BackgroundJobState.Cancelled)
            .Skip(keepLatest)
            .Select(x => x.JobId)
            .ToArray();
        var removed = 0;
        foreach (var id in removable)
        {
            if (_jobs.TryRemove(id, out var registration))
            {
                registration.Dispose();
                removed++;
            }
        }
        return removed;
    }

    private JobRegistration CreateRegistration(BackgroundJobRequest request, CancellationTokenSource? cancellation = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = Guid.NewGuid();
        var queuedAt = DateTimeOffset.UtcNow;
        var registration = new JobRegistration(id, request.Name.Trim(), request.Category, cancellation ?? new CancellationTokenSource(), queuedAt);
        _jobs[id] = registration;
        Publish(registration, BackgroundJobState.Queued, null, "Queued", null);
        TrimHistoryIfNeeded();
        return registration;
    }

    private async Task ExecuteDetachedAsync(JobRegistration registration, Func<BackgroundJobContext, CancellationToken, Task> work)
    {
        try
        {
            await ExecuteAsync(registration, work, rethrow: false).ConfigureAwait(false);
        }
        catch
        {
            // Detached jobs expose failures through their retained state and
            // ProgressChanged event; exceptions must not become unobserved.
        }
    }

    private async Task ExecuteAsync(
        JobRegistration registration,
        Func<BackgroundJobContext, CancellationToken, Task> work,
        bool rethrow)
    {
        Interlocked.Increment(ref _activeExecutions);
        var acquired = false;
        var cancellationToken = registration.Token;
        try
        {
            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            registration.StartedAt = DateTimeOffset.UtcNow;
            Publish(registration, BackgroundJobState.Running, 0, "Running", null);
            var context = new BackgroundJobContext((percent, message) =>
                Publish(registration, BackgroundJobState.Running, percent, message, null));
            await work(context, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            registration.FinishedAt = DateTimeOffset.UtcNow;
            Publish(registration, BackgroundJobState.Completed, 100, "Completed", null);
        }
        catch (OperationCanceledException)
        {
            registration.FinishedAt = DateTimeOffset.UtcNow;
            Publish(registration, BackgroundJobState.Cancelled, registration.LastProgress.Percent, "Cancelled", null);
            if (rethrow) throw;
        }
        catch (Exception ex)
        {
            registration.FinishedAt = DateTimeOffset.UtcNow;
            Publish(registration, BackgroundJobState.Failed, registration.LastProgress.Percent, ex.Message, ex);
            if (rethrow) throw;
        }
        finally
        {
            if (acquired) _concurrency.Release();
            Interlocked.Decrement(ref _activeExecutions);
            if (_disposed)
            {
                registration.Dispose();
                TryDisposeInfrastructureWhenIdle();
            }
        }
    }

    private void Publish(
        JobRegistration job,
        BackgroundJobState state,
        double? percent,
        string? message,
        Exception? error)
    {
        var update = new BackgroundJobProgress(
            job.Id,
            job.Name,
            job.Category,
            state,
            percent,
            message ?? job.LastProgress.Message,
            job.QueuedAt,
            job.StartedAt,
            job.FinishedAt,
            (state is BackgroundJobState.Queued or BackgroundJobState.Running) && !job.IsCancellationRequested,
            error);
        job.LastProgress = update;
        try { ProgressChanged?.Invoke(this, update); } catch { }
        _events?.Publish(new BackgroundJobChangedEvent(update, DateTimeOffset.UtcNow));
    }

    private void TrimHistoryIfNeeded()
    {
        if (_jobs.Count <= _historyLimit) return;
        PruneCompleted(Math.Max(1, _historyLimit / 2));
    }

    private void Validate(BackgroundJobRequest request)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);
        ArgumentNullException.ThrowIfNull(request.Work);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var job in _jobs.Values)
        {
            job.RequestCancellation();
            if (job.IsTerminal) job.Dispose();
        }
        TryDisposeInfrastructureWhenIdle();
    }

    private void TryDisposeInfrastructureWhenIdle()
    {
        if (!_disposed || Volatile.Read(ref _activeExecutions) != 0) return;
        if (Interlocked.Exchange(ref _infrastructureDisposed, 1) != 0) return;
        foreach (var job in _jobs.Values) job.Dispose();
        _concurrency.Dispose();
    }

    private sealed class JobRegistration : IDisposable
    {
        public JobRegistration(Guid id, string name, BackgroundJobCategory category, CancellationTokenSource cancellation, DateTimeOffset queuedAt)
        {
            Id = id;
            Name = name;
            Category = category;
            Cancellation = cancellation;
            QueuedAt = queuedAt;
            LastProgress = new BackgroundJobProgress(id, name, category, BackgroundJobState.Queued, null, "Queued", queuedAt, null, null, true);
        }

        public Guid Id { get; }
        public string Name { get; }
        public BackgroundJobCategory Category { get; }
        public CancellationTokenSource Cancellation { get; }
        public CancellationToken Token => Cancellation.Token;
        public bool IsCancellationRequested
        {
            get
            {
                try { return Cancellation.IsCancellationRequested; }
                catch (ObjectDisposedException) { return true; }
            }
        }
        public bool IsTerminal => LastProgress.State is BackgroundJobState.Completed or BackgroundJobState.Failed or BackgroundJobState.Cancelled;
        public DateTimeOffset QueuedAt { get; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public BackgroundJobProgress LastProgress { get; set; }

        public bool RequestCancellation()
        {
            try
            {
                if (Cancellation.IsCancellationRequested) return true;
                Cancellation.Cancel();
                return true;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Cancellation.Dispose();
        }

        private int _disposed;
    }
}
