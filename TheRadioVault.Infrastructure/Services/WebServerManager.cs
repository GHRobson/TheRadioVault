using TheRadioVault.Core.Events;
using TheRadioVault.Core.Playback;
using TheRadioVault.Services.Jobs;
using TheRadioVault.Web.Contracts;

namespace TheRadioVault.Services;

public static class WebServerManager
{
    private static readonly object Gate = new();
    private static LocalWebServerService? _server;
    private static WebServerPreferences _preferences = WebServerPreferences.Load();

    public static WebServerPreferences Preferences => _preferences;
    public static LocalWebServerService? Server => _server;

    public static void Initialize(
        DatabaseService database,
        IApplicationEventBus events,
        ILivePlaybackStateStore livePlayback,
        IBackgroundJobQueue jobs,
        IWebPlaybackController playbackController)
    {
        lock (Gate)
        {
            _server?.Dispose();
            _preferences = WebServerPreferences.Load();
            _server = new LocalWebServerService(database, _preferences, events, livePlayback, jobs, playbackController);
            if (_preferences.Enabled && _preferences.StartAutomatically)
            {
                try { _server.Start(); }
                catch { }
            }
        }
    }

    public static void Apply(WebServerPreferences preferences, bool shouldRun)
    {
        lock (Gate)
        {
            _server?.Stop();
            _preferences = preferences;
            _preferences.Enabled = shouldRun;
            _preferences.Save();
            _server?.UpdatePreferences(_preferences);
            if (shouldRun) _server?.Start();
        }
    }

    public static string? GetBroadcastUrl(long episodeId)
    {
        lock (Gate)
        {
            if (_server is null || !_server.IsRunning) return null;
            return _server.GetBroadcastUrls(episodeId).FirstOrDefault();
        }
    }

    public static string? GetSecureSetupUrl()
    {
        lock (Gate)
        {
            if (_server is null || !_server.IsRunning || !_server.IsSecure) return null;
            return _server.GetSecureSetupUrls().FirstOrDefault();
        }
    }

    public static string? GetSecureAccessUrl()
    {
        lock (Gate)
        {
            if (_server is null || !_server.IsRunning || !_server.IsSecure) return null;
            return _server.GetAccessUrls().FirstOrDefault();
        }
    }

    public static void ResetSecureCertificates()
    {
        lock (Gate)
        {
            var shouldRun = _server?.IsRunning == true;
            _server?.Stop();
            SecureWebCertificateService.ResetCertificates();
            _server?.UpdatePreferences(_preferences);
            if (shouldRun) _server?.Start();
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            _server?.Dispose();
            _server = null;
        }
    }
}
