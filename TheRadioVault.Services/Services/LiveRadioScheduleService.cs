using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Services;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Materializes an immutable, server-clock schedule from playable canonical
/// broadcasts. It deliberately never calls a progress or personal-state writer.
/// </summary>
public sealed class LiveRadioScheduleService : ILiveRadioService, IDisposable
{
    public const string MainStationKey = "main";
    public const string MainStationName = "Radio Vault Live";
    private const long MinimumProgrammeDurationMs = 5 * 60 * 1000;
    private const long FallbackProgrammeDurationMs = 2 * 60 * 60 * 1000;
    private readonly SqliteDatabase _database;
    private readonly LibraryBrowseService _library;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public LiveRadioScheduleService(SqliteDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _library = new LibraryBrowseService(database);
    }

    public async Task<LiveRadioSnapshot> GetSnapshotAsync(
        DateTimeOffset? at = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var now = (at ?? DateTimeOffset.UtcNow).ToUniversalTime();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var zone = TimeZoneInfo.Local;
            var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
            await EnsureDayAsync(localDate, zone, cancellationToken).ConfigureAwait(false);
            await EnsureDayAsync(localDate.AddDays(1), zone, cancellationToken).ConfigureAwait(false);
            var stored = await ReadScheduleAsync(now.AddDays(-1), now.AddDays(2), cancellationToken).ConfigureAwait(false);
            var currentRow = stored.FirstOrDefault(value => value.StartsAt <= now && value.EndsAt > now);
            if (currentRow is null)
            {
                await ReplaceDayAsync(localDate, zone, cancellationToken).ConfigureAwait(false);
                stored = await ReadScheduleAsync(now.AddDays(-1), now.AddDays(2), cancellationToken).ConfigureAwait(false);
                currentRow = stored.FirstOrDefault(value => value.StartsAt <= now && value.EndsAt > now);
            }

            var rows = currentRow is null
                ? stored.Where(value => value.StartsAt > now).Take(6).ToArray()
                : new[] { currentRow }.Concat(stored.Where(value => value.StartsAt >= currentRow.EndsAt).Take(5)).ToArray();
            var broadcasts = await _library.GetBroadcastsAsync(
                rows.Select(value => value.EpisodeId).Distinct().ToArray(),
                cancellationToken).ConfigureAwait(false);
            var byId = broadcasts.ToDictionary(value => value.RepresentativeEpisodeId);
            LiveRadioProgramme? Map(StoredEntry row)
            {
                if (!byId.TryGetValue(row.EpisodeId, out var broadcast)) return null;
                var position = row.StartsAt <= now && row.EndsAt > now
                    ? Math.Clamp((long)(now - row.StartsAt).TotalMilliseconds, 0, Math.Max(0, broadcast.DurationMs - 1))
                    : 0;
                return new LiveRadioProgramme(
                    row.Id,
                    row.StartsAt,
                    row.EndsAt,
                    position,
                    row.SelectionReason,
                    broadcast);
            }

            var current = currentRow is null ? null : Map(currentRow);
            var upcoming = rows
                .Where(value => currentRow is null || value.Id != currentRow.Id)
                .Select(Map)
                .Where(value => value is not null)
                .Cast<LiveRadioProgramme>()
                .Take(5)
                .ToArray();
            return new LiveRadioSnapshot(
                MainStationKey,
                MainStationName,
                zone.Id,
                now,
                stored.Count == 0 ? 0 : stored.Max(value => value.Id),
                current,
                upcoming);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureDayAsync(
        DateOnly localDate,
        TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM live_radio_schedule_entries WHERE station_key=$station AND schedule_date=$date;";
        command.Parameters.AddWithValue("$station", MainStationKey);
        command.Parameters.AddWithValue("$date", localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture) > 0)
            return;
        await GenerateDayAsync(localDate, zone, replace: false, cancellationToken).ConfigureAwait(false);
    }

    private Task ReplaceDayAsync(DateOnly localDate, TimeZoneInfo zone, CancellationToken cancellationToken)
        => GenerateDayAsync(localDate, zone, replace: true, cancellationToken);

    private async Task GenerateDayAsync(
        DateOnly localDate,
        TimeZoneInfo zone,
        bool replace,
        CancellationToken cancellationToken)
    {
        var result = await _library.BrowseAsync(
            new LibraryBrowseRequest(Limit: 10_000, NewestFirst: false),
            cancellationToken).ConfigureAwait(false);
        var candidates = result.Broadcasts
            .Where(value => value.RepresentativeEpisodeId > 0 &&
                            value.DurationMs >= MinimumProgrammeDurationMs &&
                            value.PhysicalFileCount > 0)
            .ToArray();
        if (candidates.Length == 0) return;

        var dayStart = ToUtc(localDate, TimeOnly.MinValue, zone);
        var dayEnd = ToUtc(localDate.AddDays(1), TimeOnly.MinValue, zone);
        var recentlyAired = await ReadRecentlyAiredEpisodeIdsAsync(dayStart.AddDays(-90), cancellationToken).ConfigureAwait(false);
        var usedToday = new HashSet<long>();
        var planned = new List<PlannedEntry>();
        var cursor = dayStart;
        while (cursor < dayEnd)
        {
            var localCursor = TimeZoneInfo.ConvertTime(cursor, zone);
            var available = candidates.Where(value => !usedToday.Contains(value.RepresentativeEpisodeId)).ToArray();
            if (available.Length == 0)
            {
                usedToday.Clear();
                available = candidates;
            }
            var selected = available
                .OrderByDescending(value => Score(value, localDate, localCursor, recentlyAired))
                .ThenBy(value => StableTieBreak(localDate, cursor, value.CanonicalKey))
                .First();
            usedToday.Add(selected.RepresentativeEpisodeId);
            var duration = selected.DurationMs > 0 ? selected.DurationMs : FallbackProgrammeDurationMs;
            var end = cursor.AddMilliseconds(duration);
            if (end > dayEnd) end = dayEnd;
            planned.Add(new PlannedEntry(
                cursor,
                end,
                selected,
                BuildSelectionReason(selected, localDate, localCursor)));
            cursor = end;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var dateText = localDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (replace)
        {
            await using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM live_radio_schedule_entries WHERE station_key=$station AND schedule_date=$date;";
            delete.Parameters.AddWithValue("$station", MainStationKey);
            delete.Parameters.AddWithValue("$date", dateText);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        var generated = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var entry in planned)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT OR IGNORE INTO live_radio_schedule_entries(
                    station_key,schedule_date,starts_at,ends_at,episode_id,canonical_key,selection_reason,generated_at)
                VALUES($station,$date,$starts,$ends,$episode,$canonical,$reason,$generated);
                """;
            insert.Parameters.AddWithValue("$station", MainStationKey);
            insert.Parameters.AddWithValue("$date", dateText);
            insert.Parameters.AddWithValue("$starts", entry.StartsAt.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$ends", entry.EndsAt.ToString("O", CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$episode", entry.Broadcast.RepresentativeEpisodeId);
            insert.Parameters.AddWithValue("$canonical", entry.Broadcast.CanonicalKey);
            insert.Parameters.AddWithValue("$reason", entry.SelectionReason);
            insert.Parameters.AddWithValue("$generated", generated);
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM live_radio_schedule_entries WHERE station_key=$station AND ends_at<$cutoff;";
            cleanup.Parameters.AddWithValue("$station", MainStationKey);
            cleanup.Parameters.AddWithValue("$cutoff", dayStart.AddDays(-120).ToString("O", CultureInfo.InvariantCulture));
            await cleanup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<StoredEntry>> ReadScheduleAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var result = new List<StoredEntry>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,starts_at,ends_at,episode_id,canonical_key,selection_reason
              FROM live_radio_schedule_entries
             WHERE station_key=$station AND ends_at>$from AND starts_at<$to
             ORDER BY starts_at,id;
            """;
        command.Parameters.AddWithValue("$station", MainStationKey);
        command.Parameters.AddWithValue("$from", from.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$to", to.ToString("O", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            result.Add(new StoredEntry(
                reader.GetInt64(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                reader.GetInt64(3),
                reader.GetString(4),
                reader.GetString(5)));
        return result;
    }

    private async Task<HashSet<long>> ReadRecentlyAiredEpisodeIdsAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var result = new HashSet<long>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT episode_id FROM live_radio_schedule_entries WHERE station_key=$station AND starts_at>=$since;";
        command.Parameters.AddWithValue("$station", MainStationKey);
        command.Parameters.AddWithValue("$since", since.ToString("O", CultureInfo.InvariantCulture));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) result.Add(reader.GetInt64(0));
        return result;
    }

    private static int Score(
        LibraryBroadcastSummary value,
        DateOnly stationDate,
        DateTimeOffset localCursor,
        IReadOnlySet<long> recentlyAired)
    {
        var score = 0;
        if (value.AirDate is { } date)
        {
            if (date.Month == stationDate.Month && date.Day == stationDate.Day) score += 4_000;
            if (date.DayOfWeek == stationDate.DayOfWeek) score += 1_000;
            var calendarDistance = CircularCalendarDistance(date, stationDate);
            score += Math.Max(0, 500 - calendarDistance * 40);
        }
        var slotMinute = PreferredStartMinute(value);
        if (slotMinute.HasValue)
        {
            var cursorMinute = localCursor.Hour * 60 + localCursor.Minute;
            var distance = Math.Abs(slotMinute.Value - cursorMinute);
            score += Math.Max(0, 600 - distance * 3);
        }
        if (!value.Completed && !value.InProgress) score += 220;
        if (!recentlyAired.Contains(value.RepresentativeEpisodeId)) score += 900;
        if (!string.IsNullOrWhiteSpace(value.ArtworkPath)) score += 40;
        if (!string.IsNullOrWhiteSpace(value.Description)) score += 30;
        if (!string.IsNullOrWhiteSpace(value.Title)) score += 20;
        if (value.NeedsAttention) score -= 300;
        return score;
    }

    private static int? PreferredStartMinute(LibraryBroadcastSummary value)
    {
        var text = value.BroadcastSlot?.Trim() ?? string.Empty;
        var clock = Regex.Match(text, @"(?<!\d)(?<hour>\d{1,2})(?::(?<minute>\d{2}))?\s*(?<meridiem>a\.?m\.?|p\.?m\.?)", RegexOptions.IgnoreCase);
        if (clock.Success && int.TryParse(clock.Groups["hour"].Value, out var hour))
        {
            var minute = int.TryParse(clock.Groups["minute"].Value, out var parsedMinute) ? parsedMinute : 0;
            hour %= 12;
            if (clock.Groups["meridiem"].Value.StartsWith("p", StringComparison.OrdinalIgnoreCase)) hour += 12;
            return hour * 60 + minute;
        }
        return BroadcastSlotNormalizer.Canonicalize(text) switch
        {
            "am" => 6 * 60,
            "midday" => 11 * 60,
            "pm" => 14 * 60,
            "late" => 19 * 60,
            _ when value.CollectionName.Contains("Opie", StringComparison.OrdinalIgnoreCase) => 6 * 60,
            _ when value.CollectionName.Contains("Ron and Fez", StringComparison.OrdinalIgnoreCase) => 11 * 60,
            _ when value.CollectionName.Contains("Bennington", StringComparison.OrdinalIgnoreCase) => 12 * 60,
            _ => null
        };
    }

    private static string BuildSelectionReason(
        LibraryBroadcastSummary value,
        DateOnly stationDate,
        DateTimeOffset localCursor)
    {
        var reasons = new List<string>();
        if (value.AirDate is { } date)
        {
            if (date.Month == stationDate.Month && date.Day == stationDate.Day) reasons.Add("On this date");
            if (date.DayOfWeek == stationDate.DayOfWeek) reasons.Add($"{stationDate.DayOfWeek} archive");
        }
        if (PreferredStartMinute(value) is { } minute && Math.Abs(minute - (localCursor.Hour * 60 + localCursor.Minute)) <= 180)
            reasons.Add(string.IsNullOrWhiteSpace(value.BroadcastSlot) ? "Historical show slot" : value.BroadcastSlot.Trim());
        if (!value.Completed && !value.InProgress) reasons.Add("Unheard discovery");
        return reasons.Count == 0 ? "A discovery from your Radio Vault archive" : string.Join(" · ", reasons.Distinct());
    }

    private static int CircularCalendarDistance(DateOnly candidate, DateOnly stationDate)
    {
        var referenceYear = DateTime.IsLeapYear(stationDate.Year) ? stationDate.Year : 2024;
        var candidateDay = new DateOnly(referenceYear, candidate.Month, Math.Min(candidate.Day, DateTime.DaysInMonth(referenceYear, candidate.Month))).DayOfYear;
        var stationDay = new DateOnly(referenceYear, stationDate.Month, Math.Min(stationDate.Day, DateTime.DaysInMonth(referenceYear, stationDate.Month))).DayOfYear;
        var distance = Math.Abs(candidateDay - stationDay);
        return Math.Min(distance, DateTime.IsLeapYear(referenceYear) ? 366 - distance : 365 - distance);
    }

    private static ulong StableTieBreak(DateOnly date, DateTimeOffset startsAt, string canonicalKey)
    {
        var input = Encoding.UTF8.GetBytes($"{MainStationKey}|{date:yyyy-MM-dd}|{startsAt:O}|{canonicalKey}");
        var hash = SHA256.HashData(input);
        return BitConverter.ToUInt64(hash, 0);
    }

    private static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(time), DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        var offset = zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _gate.Dispose();
    }

    private sealed record PlannedEntry(
        DateTimeOffset StartsAt,
        DateTimeOffset EndsAt,
        LibraryBroadcastSummary Broadcast,
        string SelectionReason);

    private sealed record StoredEntry(
        long Id,
        DateTimeOffset StartsAt,
        DateTimeOffset EndsAt,
        long EpisodeId,
        string CanonicalKey,
        string SelectionReason);
}
