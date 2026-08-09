namespace TheRadioVault.Client.Mobile.Platform;

public sealed record MobilePlaybackSnapshot(
    bool IsOpen,
    bool IsPlaying,
    TimeSpan Position,
    TimeSpan? Duration,
    string Error = "");

public interface IMobilePlaybackEngine : IDisposable
{
    event EventHandler<MobilePlaybackSnapshot>? StateChanged;
    event EventHandler? MediaEnded;
    MobilePlaybackSnapshot Current { get; }
    void Open(string url);
    void Play();
    void Pause();
    void Seek(TimeSpan position);
    void SetRate(double rate);
    void SetMuted(bool muted);
}

public sealed record MobilePlaybackSource(
    string Identifier,
    Func<string?, CancellationToken, Task<HttpResponseMessage>> OpenResponseAsync);

public interface IMobileStreamingPlaybackEngine
{
    void Open(MobilePlaybackSource source);
}

public interface IMobilePlaybackDiagnostics
{
    void WritePlaybackDiagnostic(string message);
}
