using System.IO.Compression;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

/// <summary>
/// Creates a verified authoritative archive backup when the latest backup is
/// older than the configured interval. The timer only checks eligibility;
/// backup work is serialized and never overlaps.
/// </summary>
public sealed class ScheduledBackupService : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string, string> _createBackup;
    private readonly string _backupDirectory;
    private readonly Func<string, bool> _verifyBackup;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusGate = new();
    private Timer? _timer;
    private WebScheduledBackupStatus _status;
    private bool _disposed;

    public ScheduledBackupService(
        TimeSpan? interval = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<string, string>? createBackup = null,
        string? backupDirectory = null,
        Func<string, bool>? verifyBackup = null)
    {
        _interval = interval ?? TimeSpan.FromDays(1);
        if (_interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _createBackup = createBackup ?? (path => new BackupService().CreateBackup(path));
        _backupDirectory = string.IsNullOrWhiteSpace(backupDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TheRadioVault",
                "Backups")
            : Path.GetFullPath(backupDirectory);
        _verifyBackup = verifyBackup ?? Verify;
        var latest = FindLatestBackup();
        _status = new WebScheduledBackupStatus(
            Enabled: false,
            IsRunning: false,
            LastCompletedAt: latest?.WrittenAt,
            NextDueAt: NextDue(latest?.WrittenAt),
            LatestBackupPath: latest?.Path ?? string.Empty,
            LastBackupVerified: latest is not null && _verifyBackup(latest.Value.Path),
            LastError: string.Empty);
    }

    public WebScheduledBackupStatus Status
    {
        get { lock (_statusGate) return _status; }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_statusGate) _status = _status with { Enabled = true };
        _timer ??= new Timer(_ => _ = RunIfDueAsync(), null, TimeSpan.Zero, TimeSpan.FromHours(1));
    }

    public void Stop()
    {
        _timer?.Dispose();
        _timer = null;
        lock (_statusGate) _status = _status with { Enabled = false, IsRunning = false };
    }

    public async Task<WebScheduledBackupStatus> RunIfDueAsync(
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = Status;
            var now = _utcNow();
            if (!force && current.LastCompletedAt.HasValue && now < current.LastCompletedAt.Value + _interval)
                return current with { NextDueAt = current.LastCompletedAt.Value + _interval };

            lock (_statusGate) _status = _status with { IsRunning = true, LastError = string.Empty };
            try
            {
                var destination = Path.Combine(
                    _backupDirectory,
                    $"scheduled-{now:yyyyMMdd-HHmmss}.trvbackup");
                var completedPath = _createBackup(destination);
                if (!_verifyBackup(completedPath))
                    throw new InvalidDataException("The scheduled backup did not contain a readable Radio Vault database snapshot.");
                var completedAt = _utcNow();
                lock (_statusGate)
                {
                    _status = _status with
                    {
                        IsRunning = false,
                        LastCompletedAt = completedAt,
                        NextDueAt = completedAt + _interval,
                        LatestBackupPath = completedPath,
                        LastBackupVerified = true,
                        LastError = string.Empty
                    };
                    return _status;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                lock (_statusGate)
                {
                    _status = _status with
                    {
                        IsRunning = false,
                        NextDueAt = _utcNow().AddHours(1),
                        LastBackupVerified = false,
                        LastError = exception.Message
                    };
                    return _status;
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _gate.Dispose();
    }

    private DateTimeOffset? NextDue(DateTimeOffset? latest)
        => latest.HasValue ? latest.Value + _interval : _utcNow();

    private (string Path, DateTimeOffset WrittenAt)? FindLatestBackup()
    {
        try
        {
            if (!Directory.Exists(_backupDirectory)) return null;
            var latest = Directory.EnumerateFiles(_backupDirectory, "*.trvbackup")
                .Select(path => (Path: path, WrittenAt: new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)))
                .OrderByDescending(value => value.WrittenAt)
                .FirstOrDefault();
            return string.IsNullOrWhiteSpace(latest.Path) ? null : latest;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Verify(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var database = archive.GetEntry("radio_vault.db");
            return database is { Length: > 0 };
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
