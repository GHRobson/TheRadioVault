using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Services;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Materialises unambiguous Research dates into the live episode projection.
/// It also migrates dates approved by older releases, where the decision was
/// durable inside research_json but had not yet reached research_broadcasts.air_date.
/// Different trusted Library dates are never replaced automatically.
/// </summary>
public static partial class ResearchDateAuthoritySynchronizer
{
    public static async Task<int> SynchronizeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        await AttachUnambiguousOrphanedResearchAsync(connection, cancellationToken).ConfigureAwait(false);

        var rows = await ReadLinkedResearchDatesAsync(connection, cancellationToken).ConfigureAwait(false);
        var updates = rows
            .GroupBy(row => row.EpisodeId)
            .Select(group => new DateUpdate(
                group.Key,
                group.Select(row => row.ResearchDate).Distinct().ToArray(),
                group.Select(row => row.EpisodeDate).FirstOrDefault(),
                group.Select(row => row.EpisodeConfidence).FirstOrDefault() ?? "Unknown"))
            .Where(candidate => candidate.Dates.Length == 1)
            .Where(CanMaterialise)
            .ToDictionary(candidate => candidate.EpisodeId);

        AddMultipartSiblings(connection, updates);
        if (updates.Count == 0) return 0;

        await using var transactionBase = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = (SqliteTransaction)transactionBase;
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var item in updates.Values)
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

            await using (var updateResearch = connection.CreateCommand())
            {
                updateResearch.Transaction = transaction;
                updateResearch.CommandText = """
                    UPDATE research_broadcasts
                       SET air_date=COALESCE(NULLIF(trim(air_date),''),$date),updated_at=$now
                     WHERE episode_id=$episode;
                    """;
                updateResearch.Parameters.AddWithValue("$date", date);
                updateResearch.Parameters.AddWithValue("$now", now);
                updateResearch.Parameters.AddWithValue("$episode", item.EpisodeId);
                await updateResearch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
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
        return updates.Count;
    }

    private static async Task<List<ResearchDateRow>> ReadLinkedResearchDatesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var rows = new List<ResearchDateRow>();
        await using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT rb.id,rb.episode_id,COALESCE(rb.air_date,''),COALESCE(rb.research_json,'{}'),
                   e.air_date,COALESCE(e.date_confidence,'Unknown')
              FROM research_broadcasts rb
              JOIN episodes e ON e.id=rb.episode_id
             WHERE rb.episode_id IS NOT NULL
             ORDER BY rb.episode_id,rb.id;
            """;
        await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!TryResolveResearchDate(reader.GetString(2), reader.GetString(3), out var researchDate, out _)) continue;
            rows.Add(new ResearchDateRow(
                reader.GetInt64(0),
                reader.GetInt64(1),
                researchDate,
                reader.IsDBNull(4) || !TryParseDate(reader.GetString(4), out var episodeDate) ? null : episodeDate,
                reader.GetString(5)));
        }
        return rows;
    }

    private static async Task AttachUnambiguousOrphanedResearchAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var researchRows = new List<OrphanResearchRow>();
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT rb.id,rb.collection_id,c.name,COALESCE(rb.source_broadcast_id,''),
                       COALESCE(rb.air_date,''),COALESCE(rb.headline,''),COALESCE(rb.research_json,'{}'),
                       COALESCE(rb.part_number,1),COALESCE(rb.confidence,0)
                  FROM research_broadcasts rb
                  JOIN collections c ON c.id=rb.collection_id
                 WHERE rb.episode_id IS NULL
                   AND NOT EXISTS(SELECT 1 FROM research_conflicts rc
                                   WHERE rc.research_broadcast_id=rb.id AND rc.resolution='unresolved')
                   AND NOT EXISTS(SELECT 1 FROM research_reconciliation_candidates rrc
                                   WHERE rrc.research_broadcast_id=rb.id
                                     AND rrc.status='pending' AND rrc.requires_review=1)
                 ORDER BY rb.id;
                """;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var json = reader.GetString(6);
                if (!TryResolveResearchDate(reader.GetString(4), json, out var date, out var approved)) continue;
                var confidence = reader.GetInt32(8);
                if (!approved && confidence < 80) continue;
                researchRows.Add(new OrphanResearchRow(
                    reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3),
                    date, reader.GetString(5), ReadLegacyOriginalFilename(json), reader.GetInt32(7)));
            }
        }
        if (researchRows.Count == 0) return;

        var episodes = new List<EpisodeCandidate>();
        await using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT e.id,e.collection_id,c.name,COALESCE(e.broadcast_uid,''),COALESCE(e.title,''),
                       COALESCE(e.part_number,1),e.air_date,COALESCE(e.date_confidence,'Unknown'),
                       COALESCE((SELECT group_concat(mf.original_filename,char(31)) FROM media_files mf
                                 WHERE mf.episode_id=e.id AND COALESCE(mf.is_missing,0)=0),'')
                  FROM episodes e
                  JOIN collections c ON c.id=e.collection_id
                 WHERE COALESCE(e.hidden,0)=0
                   AND EXISTS(SELECT 1 FROM media_files mf
                               WHERE mf.episode_id=e.id AND COALESCE(mf.is_missing,0)=0)
                 ORDER BY e.id;
                """;
            await using var reader = await read.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                DateOnly? currentDate = reader.IsDBNull(6) || !TryParseDate(reader.GetString(6), out var parsed) ? null : parsed;
                episodes.Add(new EpisodeCandidate(
                    reader.GetInt64(0), reader.GetInt32(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetInt32(5), currentDate, reader.GetString(7),
                    reader.GetString(8).Split((char)31, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
            }
        }

        var proposals = new List<(OrphanResearchRow Research, EpisodeCandidate Episode)>();
        foreach (var research in researchRows)
        {
            var candidates = episodes
                .Where(episode => episode.CollectionId == research.CollectionId)
                .Where(episode => !episode.CurrentDate.HasValue
                    || episode.CurrentDate.Value == research.Date
                    || DateConfidencePolicy.IsUncertain(episode.DateConfidence))
                .Where(episode => research.PartNumber <= 1 || episode.PartNumber == research.PartNumber)
                .Where(episode => MatchesDurableIdentity(research, episode))
                .ToArray();
            if (candidates.Length == 1) proposals.Add((research, candidates[0]));
        }

        var unique = proposals
            .GroupBy(proposal => proposal.Episode.EpisodeId)
            .Where(group => group.Count() == 1)
            .Select(group => group.Single())
            .ToArray();
        if (unique.Length == 0) return;

        await using var transactionBase = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = (SqliteTransaction)transactionBase;
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        foreach (var proposal in unique)
        {
            await using var attach = connection.CreateCommand();
            attach.Transaction = transaction;
            attach.CommandText = """
                UPDATE research_broadcasts
                   SET episode_id=$episode,research_state='in_library',existence_status='in_library',
                       attached_at=COALESCE(attached_at,$now),updated_at=$now
                 WHERE id=$research AND episode_id IS NULL;
                """;
            attach.Parameters.AddWithValue("$episode", proposal.Episode.EpisodeId);
            attach.Parameters.AddWithValue("$now", now);
            attach.Parameters.AddWithValue("$research", proposal.Research.ResearchId);
            await attach.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool MatchesDurableIdentity(OrphanResearchRow research, EpisodeCandidate episode)
    {
        if (!string.IsNullOrWhiteSpace(research.SourceBroadcastId)
            && string.Equals(research.SourceBroadcastId.Trim(), episode.BroadcastUid.Trim(), StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrWhiteSpace(research.OriginalFilename)
            && episode.OriginalFilenames.Any(filename =>
                string.Equals(filename, research.OriginalFilename, StringComparison.OrdinalIgnoreCase)))
            return true;

        var researchKey = NormalizeMatchText(research.Headline, research.CollectionName);
        if (researchKey.Length < 5) return false;
        if (string.Equals(researchKey, NormalizeMatchText(episode.Title, episode.CollectionName), StringComparison.Ordinal)) return true;
        return episode.OriginalFilenames.Any(filename =>
            string.Equals(researchKey, NormalizeMatchText(filename, episode.CollectionName), StringComparison.Ordinal));
    }

    private static void AddMultipartSiblings(SqliteConnection connection, Dictionary<long, DateUpdate> updates)
    {
        if (updates.Count == 0) return;
        var episodes = new List<MultipartEpisode>();
        using var read = connection.CreateCommand();
        read.CommandText = """
            SELECT e.id,e.collection_id,COALESCE(e.title,''),COALESCE(e.part_number,1),e.total_parts,
                   e.air_date,COALESCE(e.date_confidence,'Unknown'),
                   COALESCE((SELECT mf.path FROM media_files mf
                             WHERE mf.episode_id=e.id AND COALESCE(mf.is_missing,0)=0
                             ORDER BY COALESCE(mf.is_preferred,0) DESC,mf.id LIMIT 1),'')
              FROM episodes e
             WHERE COALESCE(e.hidden,0)=0
               AND EXISTS(SELECT 1 FROM media_files mf
                           WHERE mf.episode_id=e.id AND COALESCE(mf.is_missing,0)=0)
             ORDER BY e.id;
            """;
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            var path = reader.GetString(7);
            var title = reader.GetString(2);
            var part = reader.GetInt32(3);
            int? total = reader.IsDBNull(4) ? null : reader.GetInt32(4);
            if (part <= 1 && total is not > 1 && !MultipartMarker().IsMatch(title) && !MultipartMarker().IsMatch(path)) continue;
            episodes.Add(new MultipartEpisode(
                reader.GetInt64(0), reader.GetInt32(1), title, part, total,
                reader.IsDBNull(5) || !TryParseDate(reader.GetString(5), out var date) ? null : date,
                reader.GetString(6), path));
        }

        var groups = episodes.GroupBy(MultipartGroupKey, StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            var members = group.ToArray();
            if (members.Length < 2 || members.Select(member => member.PartNumber).Distinct().Count() < 2) continue;
            var sourceDates = members
                .Where(member => updates.ContainsKey(member.EpisodeId))
                .Select(member => updates[member.EpisodeId].Dates[0])
                .Distinct()
                .ToArray();
            if (sourceDates.Length != 1) continue;
            var date = sourceDates[0];
            if (members.Any(member => member.CurrentDate.HasValue
                && member.CurrentDate.Value != date
                && !DateConfidencePolicy.IsUncertain(member.DateConfidence))) continue;

            foreach (var member in members)
            {
                if (updates.ContainsKey(member.EpisodeId)) continue;
                var candidate = new DateUpdate(member.EpisodeId, [date], member.CurrentDate, member.DateConfidence);
                if (CanMaterialise(candidate)) updates.Add(member.EpisodeId, candidate);
            }
        }
    }

    private static string MultipartGroupKey(MultipartEpisode episode)
    {
        var filename = Path.GetFileNameWithoutExtension(episode.Path);
        var value = string.IsNullOrWhiteSpace(episode.Title) ? filename : episode.Title;
        var stem = MultipartMarker().Replace(value, " ");
        stem = NormalizeMatchText(stem, string.Empty);
        var folder = Path.GetDirectoryName(episode.Path)?.Trim() ?? string.Empty;
        return $"{episode.CollectionId}|{folder}|{episode.TotalParts ?? 0}|{stem}";
    }

    private static bool CanMaterialise(DateUpdate candidate)
    {
        if (!candidate.CurrentDate.HasValue) return true;
        if (candidate.CurrentDate.Value != candidate.Dates[0])
            return DateConfidencePolicy.IsUncertain(candidate.CurrentConfidence);
        return !DateConfidencePolicy.IsProtectedFromAutomatedParsing(candidate.CurrentConfidence);
    }

    private static bool TryResolveResearchDate(
        string structuredDate,
        string researchJson,
        out DateOnly date,
        out bool explicitlyApproved)
    {
        if (TryParseDate(structuredDate, out date))
        {
            explicitlyApproved = false;
            return true;
        }
        explicitlyApproved = TryReadApprovedLegacyDate(researchJson, out date);
        return explicitlyApproved;
    }

    private static bool TryReadApprovedLegacyDate(string json, out DateOnly date)
    {
        date = default;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!TryProperty(root, "research", out var research)
                || !TryProperty(research, "catalogue", out var catalogue)
                || !string.Equals(ReadText(catalogue, "date_review_status", "dateReviewStatus"),
                    "approved_library_date", StringComparison.OrdinalIgnoreCase)) return false;

            var candidates = new[]
                {
                    ReadText(root, "broadcast_date", "broadcastDate"),
                    ReadText(catalogue, "date_review_date", "dateReviewDate"),
                    ReadText(catalogue, "original_release_date", "originalReleaseDate")
                }
                .Where(value => TryParseDate(value, out _))
                .Select(value => { TryParseDate(value, out var parsed); return parsed; })
                .Distinct()
                .ToArray();
            if (candidates.Length != 1) return false;
            date = candidates[0];
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static string ReadLegacyOriginalFilename(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryProperty(document.RootElement, "research", out var research)
                || !TryProperty(research, "catalogue", out var catalogue)) return string.Empty;
            return ReadText(catalogue, "original_filename", "originalFilename");
        }
        catch (JsonException) { return string.Empty; }
    }

    private static bool TryProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string ReadText(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryProperty(element, name, out var value)) continue;
            return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? string.Empty : string.Empty;
        }
        return string.Empty;
    }

    private static string NormalizeMatchText(string value, string collectionName)
    {
        var stem = Path.GetFileNameWithoutExtension(value ?? string.Empty);
        stem = MultipartMarker().Replace(stem, " ");
        var normalized = new string(stem.ToLowerInvariant().Select(character => char.IsLetterOrDigit(character) ? character : ' ').ToArray());
        var tokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        var collectionTokens = new string((collectionName ?? string.Empty).ToLowerInvariant()
                .Select(character => char.IsLetterOrDigit(character) ? character : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (collectionTokens.Length > 0 && tokens.Take(collectionTokens.Length).SequenceEqual(collectionTokens))
            tokens.RemoveRange(0, collectionTokens.Length);
        if (tokens.Count > 0 && tokens[0] == "rbi") tokens.RemoveAt(0);
        if (tokens.Count > 0 && tokens[0] == "unmasked") tokens.RemoveAt(0);
        if (tokens.Count >= 2 && tokens[0] == "opie" && tokens[1] == "anthony") tokens.RemoveRange(0, 2);
        if (tokens.Count >= 2 && tokens[0] == "o" && tokens[1] == "a") tokens.RemoveRange(0, 2);
        return string.Join(' ', tokens);
    }

    private static bool TryParseDate(string value, out DateOnly date)
        => DateOnly.TryParseExact(
            value.Trim(),
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);

    [GeneratedRegex(@"\b(?:part|pt\.?)[\s_-]*0*\d+(?:[\s_-]*(?:of|/)[\s_-]*0*\d+)?\b", RegexOptions.IgnoreCase)]
    private static partial Regex MultipartMarker();

    private sealed record ResearchDateRow(
        long ResearchId,
        long EpisodeId,
        DateOnly ResearchDate,
        DateOnly? EpisodeDate,
        string EpisodeConfidence);

    private sealed record DateUpdate(
        long EpisodeId,
        DateOnly[] Dates,
        DateOnly? CurrentDate,
        string CurrentConfidence);

    private sealed record OrphanResearchRow(
        long ResearchId,
        int CollectionId,
        string CollectionName,
        string SourceBroadcastId,
        DateOnly Date,
        string Headline,
        string OriginalFilename,
        int PartNumber);

    private sealed record EpisodeCandidate(
        long EpisodeId,
        int CollectionId,
        string CollectionName,
        string BroadcastUid,
        string Title,
        int PartNumber,
        DateOnly? CurrentDate,
        string DateConfidence,
        string[] OriginalFilenames);

    private sealed record MultipartEpisode(
        long EpisodeId,
        int CollectionId,
        string Title,
        int PartNumber,
        int? TotalParts,
        DateOnly? CurrentDate,
        string DateConfidence,
        string Path);
}
