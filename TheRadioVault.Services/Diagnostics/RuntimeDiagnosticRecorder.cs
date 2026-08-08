using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Diagnostics;

/// <summary>
/// Process-wide, privacy-safe timing journal used by the connected playback
/// diagnostics pack. It intentionally stores route names and state transitions,
/// never access tokens, certificate material or full media paths.
/// </summary>
public static class RuntimeDiagnosticRecorder
{
    private const int Capacity = 600;
    private static readonly ConcurrentQueue<RuntimeDiagnosticEvent> Events = new();

    public static IDisposable Begin(
        string category,
        string operation,
        string message = "",
        IReadOnlyDictionary<string, string>? details = null)
        => new Scope(category, operation, message, details);

    public static void Record(
        string category,
        string operation,
        string outcome,
        long durationMs = 0,
        string message = "",
        IReadOnlyDictionary<string, string>? details = null)
    {
        Events.Enqueue(new RuntimeDiagnosticEvent(
            DateTimeOffset.UtcNow,
            Sanitize(category),
            Sanitize(operation),
            Sanitize(outcome),
            Math.Max(0, durationMs),
            Sanitize(message, 600),
            SanitizeDetails(details)));
        while (Events.Count > Capacity && Events.TryDequeue(out _)) { }
    }

    public static IReadOnlyList<RuntimeDiagnosticEvent> Snapshot(DateTimeOffset? since = null)
        => Events
            .Where(x => !since.HasValue || x.Timestamp >= since.Value)
            .OrderBy(x => x.Timestamp)
            .ToArray();

    private static IReadOnlyDictionary<string, string> SanitizeDetails(IReadOnlyDictionary<string, string>? source)
    {
        if (source is null || source.Count == 0) return new Dictionary<string, string>();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in source)
        {
            var key = Sanitize(pair.Key, 80);
            if (key.Contains("token", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("certificate", StringComparison.OrdinalIgnoreCase))
                continue;
            result[key] = Sanitize(pair.Value, 300);
        }
        return result;
    }

    private static string Sanitize(string? value, int maximumLength = 160)
    {
        var text = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        // Diagnostic packs are intended to be shareable. Preserve route names and
        // server identities needed for correlation, but strip ordinary local file
        // paths that can appear inside decoder or filesystem exception messages.
        text = WindowsPath.Replace(text, "<local-path>");
        text = UserHomePath.Replace(text, "<local-path>");
        return text.Length <= maximumLength ? text : text[..maximumLength] + "…";
    }

    private static readonly Regex WindowsPath = new(
        @"(?i)(?:[a-z]:\\|\\\\)[^\s""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UserHomePath = new(
        @"/(?:home|Users)/[^\s""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class Scope : IDisposable
    {
        private readonly string _category;
        private readonly string _operation;
        private readonly string _message;
        private readonly IReadOnlyDictionary<string, string>? _details;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private int _completed;

        public Scope(string category, string operation, string message, IReadOnlyDictionary<string, string>? details)
        {
            _category = category;
            _operation = operation;
            _message = message;
            _details = details;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _stopwatch.Stop();
            Record(_category, _operation, "completed", _stopwatch.ElapsedMilliseconds, _message, _details);
        }
    }
}
