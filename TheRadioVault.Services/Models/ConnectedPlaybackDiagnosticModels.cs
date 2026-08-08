namespace TheRadioVault.Services.Models;

public enum ConnectedPlaybackDiagnosticMode
{
    Quick,
    Stress
}

public enum ConnectedPlaybackDiagnosticStatus
{
    Pending,
    Running,
    Passed,
    Warning,
    Failed,
    Cancelled
}

public sealed record ConnectedPlaybackDiagnosticStep(
    string Key,
    string Title,
    ConnectedPlaybackDiagnosticStatus Status,
    string Message,
    long DurationMs,
    DateTimeOffset UpdatedAt)
{
    public string StatusText => Status.ToString();
    public string DurationText => DurationMs > 0 ? $"{DurationMs:N0} ms" : string.Empty;
}

public sealed record RuntimeDiagnosticEvent(
    DateTimeOffset Timestamp,
    string Category,
    string Operation,
    string Outcome,
    long DurationMs,
    string Message,
    IReadOnlyDictionary<string, string> Details);

public sealed record ConnectedPlaybackDiagnosticReport(
    string Format,
    int FormatVersion,
    Guid RunId,
    string SessionCode,
    ConnectedPlaybackDiagnosticMode Mode,
    string AppVersion,
    string DeviceRole,
    string DeviceName,
    string DeviceId,
    string OperatingSystem,
    string RuntimeVersion,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    ConnectedPlaybackDiagnosticStatus OverallStatus,
    string Summary,
    IReadOnlyList<ConnectedPlaybackDiagnosticStep> Steps,
    IReadOnlyList<RuntimeDiagnosticEvent> RuntimeEvents,
    IReadOnlyDictionary<string, string> Environment)
{
    public long DurationMs => Math.Max(0, (long)(CompletedAt - StartedAt).TotalMilliseconds);
}

public sealed record ConnectedPlaybackDiagnosticProgress(
    ConnectedPlaybackDiagnosticStep Step,
    string Summary);
