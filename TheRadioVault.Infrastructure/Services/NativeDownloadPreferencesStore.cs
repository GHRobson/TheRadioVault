using System.Text.Json;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services;

public sealed class NativeDownloadPreferencesStore : INativeDownloadPreferencesStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string _path;
    private readonly object _gate = new();

    public NativeDownloadPreferencesStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A native download preferences path is required.", nameof(path));
        _path = Path.GetFullPath(path);
    }

    public NativeDownloadPreferences Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return new NativeDownloadPreferences();
                return Normalize(JsonSerializer.Deserialize<NativeDownloadPreferences>(File.ReadAllText(_path), JsonOptions)
                                 ?? new NativeDownloadPreferences());
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                DiagnosticLog.Write("Native downloads", "Device download preferences could not be read; safe defaults are being used.", exception);
                return new NativeDownloadPreferences();
            }
        }
    }

    public void Save(NativeDownloadPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        lock (_gate)
        {
            var value = Normalize(preferences);
            var directory = Path.GetDirectoryName(_path)
                            ?? throw new InvalidOperationException("The native download preferences path has no parent folder.");
            Directory.CreateDirectory(directory);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
                File.Move(temporary, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private static NativeDownloadPreferences Normalize(NativeDownloadPreferences value)
    {
        value.AutomaticDownloadWatermarkEpisodeId = Math.Max(0, value.AutomaticDownloadWatermarkEpisodeId);
        value.DownloadExpiryDays = value.DownloadExpiryDays is 1 or 7 or 30 or 90 ? value.DownloadExpiryDays : 0;
        value.StorageLimitBytes = Math.Max(0, value.StorageLimitBytes);
        if (value.AutomaticDownloadsEnabled && value.AutomaticDownloadSince <= DateTimeOffset.MinValue)
            value.AutomaticDownloadSince = DateTimeOffset.UtcNow;
        return value;
    }
}
