using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

public sealed record ServerHealthSnapshot(
    DateTimeOffset GeneratedAt,
    bool Healthy,
    string OverallStatus,
    string DatabaseQuickCheck,
    string DatabaseFileName,
    long DatabaseBytes,
    long FreeStorageBytes,
    int ArchiveFolderCount,
    int TotalMediaFiles,
    int AvailableMediaFiles,
    int CloudOnlyMediaFiles,
    int MissingMediaFiles,
    bool ServerRunning,
    bool SecureAccess,
    DateTimeOffset? CertificateExpiresAt,
    int PairedClientCount,
    IReadOnlyList<WebDeviceSyncStatus> DeviceSync,
    WebScheduledBackupStatus ScheduledBackup,
    string LastServerError);

public sealed class ServerHealthDiagnosticsService
{
    private static readonly Regex QuerySecret = new(
        @"(?i)(token|access[_-]?token|authorization|password|secret)=([^&\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex BearerSecret = new(
        @"(?i)bearer\s+[A-Za-z0-9._~+\-/]+=*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LongSecret = new(
        @"\b[A-Fa-f0-9]{48,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public byte[] CreateRedactedReport(ServerHealthSnapshot snapshot, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var report = new
        {
            format = "RadioVault.ServerDiagnostics.v1",
            generatedAt = snapshot.GeneratedAt,
            radioVaultVersion = appVersion,
            environment = new
            {
                operatingSystem = Environment.OSVersion.VersionString,
                framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString()
            },
            health = new
            {
                snapshot.Healthy,
                snapshot.OverallStatus,
                snapshot.DatabaseQuickCheck,
                snapshot.DatabaseFileName,
                snapshot.DatabaseBytes,
                snapshot.FreeStorageBytes,
                snapshot.ArchiveFolderCount,
                snapshot.TotalMediaFiles,
                snapshot.AvailableMediaFiles,
                snapshot.CloudOnlyMediaFiles,
                snapshot.MissingMediaFiles,
                snapshot.ServerRunning,
                snapshot.SecureAccess,
                snapshot.CertificateExpiresAt,
                snapshot.PairedClientCount,
                scheduledBackup = new
                {
                    snapshot.ScheduledBackup.Enabled,
                    snapshot.ScheduledBackup.IsRunning,
                    snapshot.ScheduledBackup.LastCompletedAt,
                    snapshot.ScheduledBackup.NextDueAt,
                    latestBackupFile = Path.GetFileName(snapshot.ScheduledBackup.LatestBackupPath),
                    snapshot.ScheduledBackup.LastBackupVerified,
                    lastError = Redact(snapshot.ScheduledBackup.LastError)
                },
                deviceSync = snapshot.DeviceSync.Select(value => new
                {
                    device = Pseudonymize(value.ClientId),
                    value.AcknowledgedChanges,
                    value.LastAcknowledgedAt,
                    persistenceError = Redact(value.PersistenceError)
                }),
                lastServerError = Redact(snapshot.LastServerError)
            },
            privacy = new
            {
                secretsIncluded = false,
                archivePathsIncluded = false,
                mediaNamesIncluded = false,
                clientIdsPseudonymized = true
            }
        };
        return JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
    }

    public async Task ExportAsync(
        ServerHealthSnapshot snapshot,
        string appVersion,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(
                temporary,
                CreateRedactedReport(snapshot, appVersion),
                cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static string Pseudonymize(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty));
        return "device-" + Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private static string Redact(string? value)
    {
        var result = value ?? string.Empty;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            result = result.Replace(profile, "<user>", StringComparison.OrdinalIgnoreCase);
        result = QuerySecret.Replace(result, "$1=<redacted>");
        result = BearerSecret.Replace(result, "Bearer <redacted>");
        return LongSecret.Replace(result, "<redacted>");
    }
}
