using Microsoft.Data.Sqlite;
using TheRadioVault.Models;

namespace TheRadioVault.Services;

public sealed partial class DatabaseService
{
    public IReadOnlyList<ResearchFieldProvenanceRecord> GetEpisodeFieldProvenance(long episodeId)
    {
        using var connection = OpenConnection();
        return ReadFieldProvenance(connection, "episode_id", episodeId);
    }

    public IReadOnlyList<ResearchFieldProvenanceRecord> GetResearchFieldProvenance(long researchBroadcastId)
    {
        using var connection = OpenConnection();
        return ReadFieldProvenance(connection, "research_broadcast_id", researchBroadcastId);
    }

    private static IReadOnlyList<ResearchFieldProvenanceRecord> ReadFieldProvenance(
        SqliteConnection connection,
        string targetColumn,
        long targetId)
    {
        if (targetColumn is not ("episode_id" or "research_broadcast_id"))
            throw new ArgumentOutOfRangeException(nameof(targetColumn));

        var records = new List<ResearchFieldProvenanceRecord>();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id,research_broadcast_id,episode_id,field_name,value_text,source_kind,
                   source_label,import_run_id,confidence,evidence_count,protected,created_at
              FROM research_field_provenance
             WHERE {targetColumn}=$id AND active=1
             ORDER BY field_name,created_at DESC,id DESC
            """;
        command.Parameters.AddWithValue("$id", targetId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            records.Add(new ResearchFieldProvenanceRecord
            {
                Id = reader.GetInt64(0),
                ResearchBroadcastId = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                EpisodeId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                FieldName = reader.GetString(3),
                ValueText = reader.GetString(4),
                SourceKind = reader.GetString(5),
                SourceLabel = reader.GetString(6),
                ImportRunId = reader.IsDBNull(7) ? null : reader.GetInt64(7),
                Confidence = reader.GetInt32(8),
                EvidenceCount = reader.GetInt32(9),
                Protected = reader.GetInt32(10) == 1,
                CreatedAt = ParseResearchTimestamp(reader.GetString(11))
            });
        }
        return records;
    }

    private static void RecordImportFieldProvenance(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long importRunId,
        long? researchBroadcastId,
        long? episodeId,
        string fieldName,
        string value,
        bool protectedValue)
    {
        if (string.IsNullOrWhiteSpace(fieldName) || fieldName == "record_status") return;
        if (!researchBroadcastId.HasValue && !episodeId.HasValue) return;

        var now = DateTime.UtcNow.ToString("O");
        if (protectedValue && HasActiveManualProvenance(
                connection, transaction, researchBroadcastId, episodeId, fieldName))
            return;

        var sourceLabel = ReadImportPackageName(connection, transaction, importRunId);
        var (confidence, evidenceCount) = ReadResearchEvidence(
            connection,
            transaction,
            researchBroadcastId,
            episodeId);

        SupersedeFieldProvenance(
            connection,
            transaction,
            researchBroadcastId,
            episodeId,
            fieldName,
            now);

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO research_field_provenance(
                research_broadcast_id,episode_id,field_name,value_text,source_kind,source_label,
                import_run_id,confidence,evidence_count,protected,active,created_at)
            VALUES($research,$episode,$field,$value,$kind,$label,$run,$confidence,$evidence,$protected,1,$now)
            """;
        insert.Parameters.AddWithValue("$research", researchBroadcastId.HasValue ? researchBroadcastId.Value : DBNull.Value);
        insert.Parameters.AddWithValue("$episode", episodeId.HasValue ? episodeId.Value : DBNull.Value);
        insert.Parameters.AddWithValue("$field", fieldName.Trim());
        insert.Parameters.AddWithValue("$value", value ?? string.Empty);
        insert.Parameters.AddWithValue("$kind", protectedValue ? "manual" : "research_pack");
        insert.Parameters.AddWithValue("$label", protectedValue
            ? "Protected manual archive edit"
            : string.IsNullOrWhiteSpace(sourceLabel) ? "Research pack" : sourceLabel);
        insert.Parameters.AddWithValue("$run", importRunId);
        insert.Parameters.AddWithValue("$confidence", confidence);
        insert.Parameters.AddWithValue("$evidence", evidenceCount);
        insert.Parameters.AddWithValue("$protected", protectedValue ? 1 : 0);
        insert.Parameters.AddWithValue("$now", now);
        insert.ExecuteNonQuery();
    }

    private static bool HasActiveManualProvenance(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? researchBroadcastId,
        long? episodeId,
        string fieldName)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT 1 FROM research_field_provenance
             WHERE active=1 AND source_kind='manual' AND field_name=$field
               AND (($episode IS NOT NULL AND episode_id=$episode)
                    OR ($episode IS NULL AND episode_id IS NULL
                        AND $research IS NOT NULL AND research_broadcast_id=$research))
             LIMIT 1
            """;
        command.Parameters.AddWithValue("$field", fieldName.Trim());
        command.Parameters.AddWithValue("$episode", episodeId.HasValue ? episodeId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$research", researchBroadcastId.HasValue ? researchBroadcastId.Value : DBNull.Value);
        return command.ExecuteScalar() is not null;
    }

    private static void RecordManualFieldProvenance(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long episodeId,
        string fieldName,
        string value,
        string sourceLabel = "Manual metadata edit")
    {
        long? researchBroadcastId = null;
        using (var research = connection.CreateCommand())
        {
            research.Transaction = transaction;
            research.CommandText = """
                SELECT id FROM research_broadcasts
                 WHERE episode_id=$episode
                 ORDER BY updated_at DESC,id DESC LIMIT 1
                """;
            research.Parameters.AddWithValue("$episode", episodeId);
            var result = research.ExecuteScalar();
            if (result is not null && result != DBNull.Value)
                researchBroadcastId = Convert.ToInt64(result);
        }

        var now = DateTime.UtcNow.ToString("O");
        SupersedeFieldProvenance(
            connection,
            transaction,
            researchBroadcastId,
            episodeId,
            fieldName,
            now);

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO research_field_provenance(
                research_broadcast_id,episode_id,field_name,value_text,source_kind,source_label,
                confidence,evidence_count,protected,active,created_at)
            VALUES($research,$episode,$field,$value,'manual',$label,100,0,1,1,$now)
            """;
        insert.Parameters.AddWithValue("$research", researchBroadcastId.HasValue ? researchBroadcastId.Value : DBNull.Value);
        insert.Parameters.AddWithValue("$episode", episodeId);
        insert.Parameters.AddWithValue("$field", fieldName.Trim());
        insert.Parameters.AddWithValue("$value", value ?? string.Empty);
        insert.Parameters.AddWithValue("$label", sourceLabel);
        insert.Parameters.AddWithValue("$now", now);
        insert.ExecuteNonQuery();
    }

    private static void SupersedeFieldProvenance(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? researchBroadcastId,
        long? episodeId,
        string fieldName,
        string now)
    {
        using var supersede = connection.CreateCommand();
        supersede.Transaction = transaction;
        supersede.CommandText = """
            UPDATE research_field_provenance
               SET active=0,superseded_at=$now
             WHERE active=1
               AND field_name=$field
               AND (($episode IS NOT NULL AND episode_id=$episode)
                    OR ($episode IS NULL AND episode_id IS NULL
                        AND $research IS NOT NULL AND research_broadcast_id=$research))
            """;
        supersede.Parameters.AddWithValue("$now", now);
        supersede.Parameters.AddWithValue("$field", fieldName.Trim());
        supersede.Parameters.AddWithValue("$episode", episodeId.HasValue ? episodeId.Value : DBNull.Value);
        supersede.Parameters.AddWithValue("$research", researchBroadcastId.HasValue ? researchBroadcastId.Value : DBNull.Value);
        supersede.ExecuteNonQuery();
    }

    private static (int Confidence, int EvidenceCount) ReadResearchEvidence(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long? researchBroadcastId,
        long? episodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT rb.confidence,
                   (SELECT COUNT(*) FROM research_sources rs WHERE rs.research_broadcast_id=rb.id)
              FROM research_broadcasts rb
             WHERE ($research IS NOT NULL AND rb.id=$research)
                OR ($research IS NULL AND $episode IS NOT NULL AND rb.episode_id=$episode)
             ORDER BY CASE WHEN $research IS NOT NULL AND rb.id=$research THEN 0 ELSE 1 END,
                      rb.updated_at DESC,rb.id DESC
             LIMIT 1
            """;
        command.Parameters.AddWithValue("$research", researchBroadcastId.HasValue ? researchBroadcastId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$episode", episodeId.HasValue ? episodeId.Value : DBNull.Value);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? (reader.GetInt32(0), reader.GetInt32(1))
            : (0, 0);
    }

    private static string ReadImportPackageName(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long importRunId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT package_name FROM research_import_runs WHERE id=$id";
        command.Parameters.AddWithValue("$id", importRunId);
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }
}
