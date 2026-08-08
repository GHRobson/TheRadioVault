using TheRadioVault.Core.Services;
using TheRadioVault.Data.Database;
using TheRadioVault.Services;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Desktop.Avalonia.Local;

public sealed class AvaloniaLocalLibraryMaintenanceService : ILibraryMaintenanceService, IDisposable
{
    private readonly LibraryScannerService _scanner;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly object _stateGate = new();
    private LibraryMaintenanceSnapshot _current = new(
        false, false, string.Empty, null, null,
        "Ready for a manual local Library scan.",
        0, 0, 0, 0, 0, 0, 0, 0, 0);
    private bool _disposed;

    public AvaloniaLocalLibraryMaintenanceService(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        var legacyDatabase = new DatabaseService(database);
        legacyDatabase.Initialize();
        _scanner = new LibraryScannerService(legacyDatabase, new FilenameParserService());
    }

    public bool IsAvailable => !_disposed;

    public Task<LibraryMaintenanceSnapshot> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_stateGate) return Task.FromResult(_current);
    }

    public async Task<LibraryMaintenanceSnapshot> ScanAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AvaloniaLocalLibraryMaintenanceService));
        if (!await _scanGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            lock (_stateGate) return _current;
        }

        var startedAt = DateTimeOffset.UtcNow;
        Update(new LibraryMaintenanceSnapshot(
            true, true, "manual-local", startedAt, null,
            "Scanning registered local archive folders…",
            0, 0, 0, 0, 0, 0, 0, 0, 0));

        try
        {
            var progress = new Progress<string>(message =>
            {
                lock (_stateGate) _current = _current with { Message = message };
            });
            var result = await Task.Run(
                () => _scanner.ScanAll(progress, cancellationToken),
                cancellationToken).ConfigureAwait(false);
            var completedAt = DateTimeOffset.UtcNow;
            var snapshot = new LibraryMaintenanceSnapshot(
                false, true, "manual-local", startedAt, completedAt,
                $"Local Library scan completed: {result.FilesFound:N0} files checked.",
                result.FilesFound,
                result.Added,
                result.Updated,
                result.Unchanged,
                result.Errors,
                result.CanonicalBroadcastsAdded,
                result.CanonicalRecordingsAdded,
                result.CanonicalEpisodesMapped,
                result.CanonicalItemsNeedingReview);
            Update(snapshot);
            return snapshot;
        }
        catch (OperationCanceledException)
        {
            var cancelled = new LibraryMaintenanceSnapshot(
                false, false, "manual-local", startedAt, DateTimeOffset.UtcNow,
                "The local Library scan was cancelled.",
                0, 0, 0, 0, 0, 0, 0, 0, 0);
            Update(cancelled);
            throw;
        }
        catch (Exception exception)
        {
            var failed = new LibraryMaintenanceSnapshot(
                false, false, "manual-local", startedAt, DateTimeOffset.UtcNow,
                $"The local Library scan failed: {exception.Message}",
                0, 0, 0, 0, 1, 0, 0, 0, 0);
            Update(failed);
            throw;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private void Update(LibraryMaintenanceSnapshot snapshot)
    {
        lock (_stateGate) _current = snapshot;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _scanGate.Dispose();
    }
}
