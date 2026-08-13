using System.Text.Json;

namespace TheRadioVault.Web.Services;

public enum WebConflictDomain
{
    Progress,
    ListeningStatus,
    Favourite,
    Moment,
    Queue
}

public enum WebConflictResolution
{
    Accept,
    Duplicate,
    RejectStale,
    RejectClockSkew,
    Append,
    ServerAuthoritative
}

public sealed record WebConflictDecision(
    WebConflictResolution Resolution,
    DateTimeOffset EffectiveAt,
    string Message)
{
    public bool Accepted => Resolution is WebConflictResolution.Accept or WebConflictResolution.Append;
}

/// <summary>
/// One explicit cross-device conflict matrix. Progress advances monotonically
/// unless the current playback generation performs an explicit seek;
/// favourites and listened state use last-action-wins by captured time;
/// Moments append by mutation id; and queue order remains server-authoritative.
/// </summary>
public static class PersonalStateConflictPolicy
{
    private static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromMinutes(5);

    public static WebConflictDecision Decide(
        WebConflictDomain domain,
        DateTimeOffset? capturedAt,
        DateTimeOffset receivedAt,
        DateTimeOffset? canonicalAt = null,
        string incomingTieBreaker = "",
        string canonicalTieBreaker = "",
        bool duplicate = false)
    {
        var received = receivedAt.ToUniversalTime();
        if (duplicate)
            return new(WebConflictResolution.Duplicate, canonicalAt ?? received,
                "This change was already applied.");

        if (domain == WebConflictDomain.Moment)
            return new(WebConflictResolution.Append, EffectiveAt(capturedAt, received),
                "Moments append independently and are deduplicated by mutation id.");
        if (domain == WebConflictDomain.Queue)
            return new(WebConflictResolution.ServerAuthoritative, received,
                "Queue order is serialized by the server and is not merged from offline snapshots.");

        if (capturedAt is { } rawCaptured && rawCaptured.ToUniversalTime() > received + MaximumFutureClockSkew)
            return new(WebConflictResolution.RejectClockSkew, received,
                "The change was rejected because its device timestamp is too far in the future.");

        var effective = EffectiveAt(capturedAt, received);
        if (!canonicalAt.HasValue)
            return new(WebConflictResolution.Accept, effective, "The change is the first known decision for this field.");

        var comparison = effective.CompareTo(canonicalAt.Value.ToUniversalTime());
        if (comparison > 0)
            return new(WebConflictResolution.Accept, effective, "The newer captured action replaces the earlier decision.");
        if (comparison < 0)
            return new(WebConflictResolution.RejectStale, effective,
                "A newer action from this or another device is already authoritative.");

        comparison = string.Compare(incomingTieBreaker, canonicalTieBreaker, StringComparison.Ordinal);
        return comparison > 0
            ? new(WebConflictResolution.Accept, effective,
                "The actions had the same timestamp; the deterministic device key selected this decision.")
            : new(WebConflictResolution.RejectStale, effective,
                "The actions had the same timestamp; the deterministic device key retained the authoritative decision.");
    }

    public static DateTimeOffset EffectiveAt(DateTimeOffset? capturedAt, DateTimeOffset receivedAt)
    {
        var received = receivedAt.ToUniversalTime();
        if (capturedAt is not { } captured) return received;
        captured = captured.ToUniversalTime();
        return captured > received + MaximumFutureClockSkew ? received : captured;
    }
}

internal sealed record WebPersonalStateDecision(
    string Domain,
    long EpisodeId,
    string Value,
    DateTimeOffset EffectiveAt,
    string TieBreaker);

/// <summary>
/// Durable per-field clocks make offline decisions converge regardless of the
/// order in which phones and computers reconnect to the server.
/// </summary>
internal sealed class WebPersonalStateDecisionLedger
{
    private readonly Dictionary<string, WebPersonalStateDecision> _decisions = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly string _path;
    private string _persistenceError = string.Empty;

    public WebPersonalStateDecisionLedger(string? path = null)
    {
        _path = path?.Trim() ?? string.Empty;
        Load();
    }

    public string PersistenceError
    {
        get { lock (_gate) return _persistenceError; }
    }

    public WebConflictDecision TryApply(
        WebConflictDomain domain,
        long episodeId,
        string value,
        DateTimeOffset? capturedAt,
        DateTimeOffset receivedAt,
        string clientId,
        string mutationId,
        Func<Models.WebMutationResult> apply,
        out Models.WebMutationResult? result)
    {
        ArgumentNullException.ThrowIfNull(apply);
        result = null;
        if (domain is not (WebConflictDomain.Favourite or WebConflictDomain.ListeningStatus))
            return PersonalStateConflictPolicy.Decide(domain, capturedAt, receivedAt);

        var domainName = domain.ToString();
        var key = $"{domainName}:{episodeId}";
        var tieBreaker = $"{clientId.Trim()}|{mutationId.Trim()}";
        lock (_gate)
        {
            _decisions.TryGetValue(key, out var current);
            var decision = PersonalStateConflictPolicy.Decide(
                domain,
                capturedAt,
                receivedAt,
                current?.EffectiveAt,
                tieBreaker,
                current?.TieBreaker ?? string.Empty,
                duplicate: current is not null &&
                           current.Value == value &&
                           current.TieBreaker == tieBreaker);
            if (!decision.Accepted) return decision;

            result = apply();
            if (!result.Changed) return decision;
            _decisions[key] = new WebPersonalStateDecision(
                domainName, episodeId, value, decision.EffectiveAt, tieBreaker);
            SaveUnsafe();
            return decision;
        }
    }

    private void Load()
    {
        if (_path.Length == 0 || !File.Exists(_path)) return;
        lock (_gate)
        {
            try
            {
                var values = JsonSerializer.Deserialize<WebPersonalStateDecision[]>(File.ReadAllBytes(_path)) ?? [];
                foreach (var value in values)
                    _decisions[$"{value.Domain}:{value.EpisodeId}"] = value;
                _persistenceError = string.Empty;
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                _persistenceError = exception.Message;
            }
        }
    }

    private void SaveUnsafe()
    {
        if (_path.Length == 0) return;
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(
                _decisions.Values.OrderBy(value => value.Domain).ThenBy(value => value.EpisodeId).ToArray());
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, _path, overwrite: true);
            _persistenceError = string.Empty;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _persistenceError = exception.Message;
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
