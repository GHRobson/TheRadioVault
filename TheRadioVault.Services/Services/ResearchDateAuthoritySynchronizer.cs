using System.Globalization;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Services;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Materialises an unambiguous date from the durable Research ledger into the
/// live episode projection. It never replaces a different trusted Library date;
/// that remains a genuine conflict for a person to resolve.
/// </summary>
public static class ResearchDateAuthoritySynchronizer
{
    public static async Task<int> SynchronizeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        var rows = new List<ResearchDateRow>();
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT rb.id,rb.episode_id,rb.air_date,e.air_date,
                       COALESCE(e.date_confidence,'Unknown')
                  FROM research_broadcasts rb
                  JOIN episodes e ON e.id=rb.episode_id
                 WHERE rb.episode_id IS NOT NULL
                   AND rb.air_date IS NOT NULL
                   AND trim(rb.air_date)<>''
                 ORDER BY rb.episode_id,rb.id;
                """;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!TryParseDate(reader.GetString(2), out var researchDate)) continue;
                rows.Add(new ResearchDateRow(
                    reader.GetInt64(1),
                    researchDate,
                    reader.IsDBNull(3) || !TryParseDate(reader.GetString(3), out var episodeDate) ? null : episodeDate,
                    reader.GetString(4)));
            }
        }

        var updates = rows
            .GroupBy(row => row.EpisodeId)
            .Select(group => new
            {
                EpisodeId = group.Key,
                Dates = group.Select(row => row.ResearchDate).Distinct().ToArray(),
                CurrentDate = group.Select(row => row.EpisodeDate).FirstOrDefault(),
                CurrentConfidence = group.Select(row => row.EpisodeConfidence).FirstOrDefault() ?? "Unknown"
            })
            .Where(candidate => candidate.Dates.Length == 1)
            .Where(candidate => !candidate.CurrentDate.HasValue
                || candidate.CurrentDate.Value == candidate.Dates[0]
                || DateConfidencePolicy.IsUncertain(candidate.CurrentConfidence))
            .Where(candidate => !candidate.CurrentDate.HasValue
                || candidate.CurrentDate.Value != candidate.Dates[0]
                || !DateConfidencePolicy.IsProtectedFromAutomatedParsing(candidate.CurrentConfidence))
            .ToArray();

        if (updates.Length == 0) return 0;

        await using var transactionBase = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = (SqliteTransaction)transactionBase;
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var item in updates)
        {
            var date = item.Dates[0].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            await using (var updateEpisode = connection.CreateCommand())
            {
                updateEpisode.Transaction = transaction;
                updateEpisode.CommandText = """
                    UPDATE episodes
                       SET air_date=$date,
                           date_confidence='Research authoritative',
                           metadata_confidence=CASE WHEN metadata_confidence<90 THEN 90 ELSE metadata_confidence END,
                           metadata_confidence_reason=CASE
                               WHEN trim(COALESCE(metadata_confidence_reason,''))='' THEN 'Date restored from the durable Research ledger'
                               ELSE metadata_confidence_reason END,
                           updated_at=$now
                     WHERE id=$episode;
                    """;
                updateEpisode.Parameters.AddWithValue("$date", date);
                updateEpisode.Parameters.AddWithValue("$now", now);
                updateEpisode.Parameters.AddWithValue("$episode", item.EpisodeId);
                await updateEpisode.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using var updateCanonical = connection.CreateCommand();
            updateCanonical.Transaction = transaction;
            updateCanonical.CommandText = """
                UPDATE canonical_broadcasts
                   SET air_date=$date,confidence_score=CASE WHEN confidence_score<99 THEN 99 ELSE confidence_score END
                 WHERE canonical_key IN (
                       SELECT canonical_key FROM episode_canonical_map WHERE episode_id=$episode);
                """;
            updateCanonical.Parameters.AddWithValue("$date", date);
            updateCanonical.Parameters.AddWithValue("$episode", item.EpisodeId);
            await updateCanonical.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return updates.Length;
    }

    private static bool TryParseDate(string value, out DateOnly date)
        => DateOnly.TryParseExact(
            value.Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    private sealed record ResearchDateRow(
        long EpisodeId,
        DateOnly ResearchDate,
        DateOnly? EpisodeDate,
        string EpisodeConfidence);
}
