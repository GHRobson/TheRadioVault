using Microsoft.Data.Sqlite;
using System.Text.Json;
using TheRadioVault.Models;

namespace TheRadioVault.Services;

public sealed partial class DatabaseService
{
    private static readonly HashSet<string> RollbackEpisodeFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "headline", "summary", "station", "archive_notes", "hosts", "guests",
        "callers", "mentioned_people", "topics", "sources", "moments"
    };

    public IReadOnlyList<ResearchImportRollbackRunRecord> GetResearchImportRollbacks(long importRunId)
    {
        using var connection = OpenConnection();
        var result = new List<ResearchImportRollbackRunRecord>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,import_run_id,scope,record_identity,restored_count,blocked_count,created_at
              FROM research_import_rollbacks
             WHERE import_run_id=$run
             ORDER BY created_at DESC,id DESC
            """;
        command.Parameters.AddWithValue("$run", importRunId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ResearchImportRollbackRunRecord
            {
                Id = reader.GetInt64(0),
                ImportRunId = reader.GetInt64(1),
                Scope = reader.GetString(2),
                RecordIdentity = reader.GetString(3),
                RestoredCount = reader.GetInt32(4),
                BlockedCount = reader.GetInt32(5),
                CreatedAt = ParseResearchTimestamp(reader.GetString(6))
            });
        }
        return result;
    }

    public ResearchImportRollbackPreview PreviewResearchImportRollback(
        long importRunId,
        long? episodeId = null,
        long? researchBroadcastId = null,
        string? targetIdentity = null)
    {
        using var connection = OpenConnection();
        var run = ReadImportRollbackHeader(connection, importRunId);
        if (run is null) throw new InvalidOperationException("The selected research import no longer exists.");
        if (run.Status == "rolled_back" && !episodeId.HasValue && !researchBroadcastId.HasValue)
            throw new InvalidOperationException("This import has already been rolled back.");

        var snapshotPath = ReadRollbackSnapshotPath(connection, importRunId);
        var snapshotAvailable = !string.IsNullOrWhiteSpace(snapshotPath) && File.Exists(snapshotPath);
        var changes = ReadRollbackChanges(connection, importRunId, episodeId, researchBroadcastId, targetIdentity);
        var items = new List<ResearchImportRollbackItem>();

        foreach (var change in changes)
        {
            // Durable research records are evaluated once, as a complete snapshot-backed unit.
            if (change.FieldName == "research_record") continue;

            if (WasImportChangeRestored(connection, change.Id))
            {
                items.Add(new ResearchImportRollbackItem
                {
                    ChangeId = change.Id,
                    EpisodeId = change.EpisodeId,
                    ResearchBroadcastId = change.ResearchBroadcastId,
                    RecordIdentity = change.RecordIdentity,
                    FieldName = change.FieldName,
                    BeforeValue = change.BeforeValue,
                    AfterValue = change.AfterValue,
                    CurrentValue = ReadCurrentRollbackValue(connection, change),
                    Status = "already_reverted",
                    Reason = "This field decision was restored by an earlier guarded rollback."
                });
                continue;
            }

            if (change.Decision is not ("applied" or "merged" or "created"))
            {
                items.Add(new ResearchImportRollbackItem
                {
                    ChangeId = change.Id,
                    EpisodeId = change.EpisodeId,
                    ResearchBroadcastId = change.ResearchBroadcastId,
                    RecordIdentity = change.RecordIdentity,
                    FieldName = change.FieldName,
                    BeforeValue = change.BeforeValue,
                    AfterValue = change.AfterValue,
                    CurrentValue = ReadCurrentRollbackValue(connection, change),
                    Status = "preserved",
                    Reason = change.Decision switch
                    {
                        "protected" => "The import did not overwrite this protected manual value.",
                        "preserved" => "The import deliberately kept the existing value.",
                        "retained_missing" => "The durable missing-broadcast record is handled separately from episode fields.",
                        "ambiguous" => "The import created a review candidate rather than changing an episode.",
                        _ => "The import did not change this field."
                    }
                });
                continue;
            }

            if (!change.EpisodeId.HasValue || !RollbackEpisodeFields.Contains(change.FieldName))
            {
                items.Add(new ResearchImportRollbackItem
                {
                    ChangeId = change.Id,
                    EpisodeId = change.EpisodeId,
                    ResearchBroadcastId = change.ResearchBroadcastId,
                    RecordIdentity = change.RecordIdentity,
                    FieldName = change.FieldName,
                    BeforeValue = change.BeforeValue,
                    AfterValue = change.AfterValue,
                    CurrentValue = ReadCurrentRollbackValue(connection, change),
                    Status = "preserved",
                    Reason = "This ledger entry does not directly replace an episode field. Its research record is evaluated separately."
                });
                continue;
            }

            var current = ReadCurrentRollbackValue(connection, change);
            var userModified = IsEpisodeUserModified(connection, change.EpisodeId.Value);
            var currentNormalized = NormalizeRollbackComparison(change.FieldName, current);
            var importedNormalized = NormalizeRollbackComparison(change.FieldName, change.AfterValue);
            var beforeNormalized = NormalizeRollbackComparison(change.FieldName, change.BeforeValue);
            var status = "blocked";
            var reason = "The current value no longer matches the imported value, so a later edit or import is protected.";

            if (string.Equals(currentNormalized, beforeNormalized, StringComparison.Ordinal))
            {
                status = "already_reverted";
                reason = "The field already contains its pre-import value.";
            }
            else if (userModified)
            {
                reason = "This broadcast is marked as manually edited. Guarded rollback will not replace its current metadata automatically.";
            }
            else if (string.Equals(currentNormalized, importedNormalized, StringComparison.Ordinal))
            {
                status = "safe";
                reason = "The field still matches this import exactly and can be restored without overwriting later work.";
            }

            items.Add(new ResearchImportRollbackItem
            {
                ChangeId = change.Id,
                EpisodeId = change.EpisodeId,
                ResearchBroadcastId = change.ResearchBroadcastId,
                RecordIdentity = change.RecordIdentity,
                FieldName = change.FieldName,
                BeforeValue = change.BeforeValue,
                AfterValue = change.AfterValue,
                CurrentValue = current,
                Status = status,
                Reason = reason
            });
        }

        foreach (var researchId in changes
                     .Where(x => x.ResearchBroadcastId.HasValue)
                     .Select(x => x.ResearchBroadcastId!.Value)
                     .Distinct())
        {
            if (researchBroadcastId.HasValue && researchId != researchBroadcastId.Value) continue;
            var identity = changes.FirstOrDefault(x => x.ResearchBroadcastId == researchId)?.RecordIdentity ?? targetIdentity ?? string.Empty;
            items.Add(BuildResearchRecordRollbackItem(
                connection,
                snapshotPath,
                snapshotAvailable,
                importRunId,
                researchId,
                identity));
        }

        return new ResearchImportRollbackPreview
        {
            ImportRunId = importRunId,
            PackageName = run.PackageName,
            EpisodeId = episodeId,
            ResearchBroadcastId = researchBroadcastId,
            TargetIdentity = !string.IsNullOrWhiteSpace(targetIdentity)
                ? targetIdentity.Trim()
                : episodeId.HasValue || researchBroadcastId.HasValue
                    ? changes.FirstOrDefault()?.RecordIdentity ?? string.Empty
                    : string.Empty,
            Items = items
        };
    }

    public ResearchImportRollbackResult RollbackResearchImport(
        long importRunId,
        long? episodeId = null,
        long? researchBroadcastId = null,
        string? targetIdentity = null)
    {
        // The preview is intentionally rebuilt immediately before the transaction.
        // A field that changed after the user opened the window is therefore blocked.
        var preview = PreviewResearchImportRollback(importRunId, episodeId, researchBroadcastId, targetIdentity);
        if (!preview.CanApply)
            return new ResearchImportRollbackResult
            {
                Applied = 0,
                Blocked = preview.BlockedCount,
                AlreadyRestored = preview.AlreadyRestoredCount,
                Preserved = preview.PreservedCount,
                Partial = preview.BlockedCount > 0
            };

        using var connection = OpenConnection();
        var snapshotPath = ReadRollbackSnapshotPath(connection, importRunId);
        var hasResearchRecordRestore = preview.Items.Any(x => x.Status == "safe" && x.FieldName == "research_record");
        if (hasResearchRecordRestore)
        {
            if (string.IsNullOrWhiteSpace(snapshotPath) || !File.Exists(snapshotPath))
                throw new InvalidOperationException("The pre-import safety snapshot is no longer available.");
            using var attach = connection.CreateCommand();
            attach.CommandText = "ATTACH DATABASE $path AS rollback_snapshot";
            attach.Parameters.AddWithValue("$path", snapshotPath);
            attach.ExecuteNonQuery();
        }

        using var transaction = connection.BeginTransaction();
        try
        {
            var now = DateTime.UtcNow.ToString("O");
            var rollbackId = BeginResearchImportRollback(
                connection,
                transaction,
                importRunId,
                preview.IsSingleBroadcast ? "record" : "entire_import",
                preview.TargetIdentity,
                now);

            var result = new ResearchImportRollbackResult
            {
                RollbackId = rollbackId,
                Blocked = preview.BlockedCount,
                AlreadyRestored = preview.AlreadyRestoredCount,
                Preserved = preview.PreservedCount
            };

            foreach (var item in preview.Items.OrderByDescending(x => x.FieldName == "research_record"))
            {
                if (item.Status != "safe")
                {
                    if (item.ChangeId.HasValue && item.Status == "blocked")
                        RecordRollbackOutcome(connection, transaction, rollbackId, item.ChangeId.Value, "blocked", item.Reason, item.CurrentValue, now);
                    continue;
                }

                if (item.FieldName == "research_record")
                {
                    RestoreResearchRecordFromSnapshot(connection, transaction, importRunId, item.ResearchBroadcastId!.Value);
                    result.Applied++;
                    continue;
                }

                if (!item.ChangeId.HasValue || !item.EpisodeId.HasValue) continue;
                var liveChange = ReadImportChange(connection, transaction, item.ChangeId.Value);
                if (liveChange is null) continue;
                var current = ReadCurrentRollbackValue(connection, transaction, liveChange);
                if (IsEpisodeUserModified(connection, transaction, liveChange.EpisodeId!.Value)
                    || !string.Equals(
                        NormalizeRollbackComparison(liveChange.FieldName, current),
                        NormalizeRollbackComparison(liveChange.FieldName, liveChange.AfterValue),
                        StringComparison.Ordinal))
                {
                    result.Blocked++;
                    RecordRollbackOutcome(connection, transaction, rollbackId, liveChange.Id, "blocked",
                        "The field changed after rollback preview and was protected.", current, now);
                    continue;
                }

                ApplyEpisodeRollbackValue(connection, transaction, liveChange);
                RecordRollbackOutcome(connection, transaction, rollbackId, liveChange.Id, "restored",
                    "Restored to the value captured before this import.", liveChange.BeforeValue, now);
                RecordRollbackProvenance(connection, transaction, importRunId, liveChange, now);
                result.Applied++;
            }

            result.Partial = preview.IsSingleBroadcast || result.Blocked > 0;
            CompleteResearchImportRollback(connection, transaction, importRunId, rollbackId, result, preview, now);
            transaction.Commit();
            return result;
        }
        catch
        {
            try { transaction.Rollback(); }
            catch { /* Preserve the rollback failure. */ }
            throw;
        }
        finally
        {
            if (hasResearchRecordRestore)
            {
                try
                {
                    using var detach = connection.CreateCommand();
                    detach.CommandText = "DETACH DATABASE rollback_snapshot";
                    detach.ExecuteNonQuery();
                }
                catch
                {
                    // Connection disposal will release an attached snapshot if a provider
                    // refuses DETACH after a failed transaction.
                }
            }
        }
    }

    private static ResearchImportRunRecord? ReadImportRollbackHeader(SqliteConnection connection, long importRunId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,package_name,status,rollback_json,restored_change_count,
                   blocked_rollback_count,last_rollback_at
            FROM research_import_runs WHERE id=$id
            """;
        command.Parameters.AddWithValue("$id", importRunId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var rollbackJson = reader.GetString(3);
        var record = new ResearchImportRunRecord
        {
            Id = reader.GetInt64(0),
            PackageName = reader.GetString(1),
            Status = reader.GetString(2),
            RestoredChangeCount = reader.GetInt32(4),
            BlockedRollbackCount = reader.GetInt32(5),
            LastRollbackAt = reader.IsDBNull(6) ? null : ParseResearchTimestamp(reader.GetString(6))
        };
        try
        {
            using var document = JsonDocument.Parse(rollbackJson);
            record.RollbackDataCaptured = document.RootElement.TryGetProperty("SnapshotPath", out var path)
                                          && File.Exists(path.GetString());
        }
        catch
        {
            record.RollbackDataCaptured = false;
        }
        return record;
    }

    private static string ReadRollbackSnapshotPath(SqliteConnection connection, long importRunId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT rollback_json FROM research_import_runs WHERE id=$id";
        command.Parameters.AddWithValue("$id", importRunId);
        var json = Convert.ToString(command.ExecuteScalar()) ?? "{}";
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("SnapshotPath", out var path)
                ? path.GetString() ?? string.Empty
                : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<ResearchImportChangeRecord> ReadRollbackChanges(
        SqliteConnection connection,
        long importRunId,
        long? episodeId,
        long? researchBroadcastId,
        string? targetIdentity)
    {
        var result = new List<ResearchImportChangeRecord>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,import_run_id,research_broadcast_id,episode_id,record_identity,
                   field_name,before_value,after_value,decision,reason,created_at
            FROM research_import_changes
            WHERE import_run_id=$run
              AND ($episode IS NULL OR episode_id=$episode)
              AND ($research IS NULL OR research_broadcast_id=$research)
              AND ($identity='' OR record_identity=$identity)
            ORDER BY id
            """;
        command.Parameters.AddWithValue("$run", importRunId);
        command.Parameters.AddWithValue("$episode", episodeId.HasValue ? episodeId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$research", researchBroadcastId.HasValue ? researchBroadcastId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$identity", targetIdentity?.Trim() ?? string.Empty);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ResearchImportChangeRecord
            {
                Id = reader.GetInt64(0),
                ImportRunId = reader.GetInt64(1),
                ResearchBroadcastId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                EpisodeId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                RecordIdentity = reader.GetString(4),
                FieldName = reader.GetString(5),
                BeforeValue = reader.GetString(6),
                AfterValue = reader.GetString(7),
                Decision = reader.GetString(8),
                Reason = reader.GetString(9),
                CreatedAt = ParseResearchTimestamp(reader.GetString(10))
            });
        }
        return result;
    }

    private static ResearchImportChangeRecord? ReadImportChange(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long changeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,import_run_id,research_broadcast_id,episode_id,record_identity,
                   field_name,before_value,after_value,decision,reason,created_at
            FROM research_import_changes WHERE id=$id
            """;
        command.Parameters.AddWithValue("$id", changeId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        return new ResearchImportChangeRecord
        {
            Id = reader.GetInt64(0),
            ImportRunId = reader.GetInt64(1),
            ResearchBroadcastId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
            EpisodeId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
            RecordIdentity = reader.GetString(4),
            FieldName = reader.GetString(5),
            BeforeValue = reader.GetString(6),
            AfterValue = reader.GetString(7),
            Decision = reader.GetString(8),
            Reason = reader.GetString(9),
            CreatedAt = ParseResearchTimestamp(reader.GetString(10))
        };
    }

    private static bool WasImportChangeRestored(SqliteConnection connection, long changeId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM research_import_rollback_changes
            WHERE import_change_id=$change AND outcome='restored' LIMIT 1
            """;
        command.Parameters.AddWithValue("$change", changeId);
        return command.ExecuteScalar() is not null;
    }

    private static bool IsEpisodeUserModified(SqliteConnection connection, long episodeId)
        => IsEpisodeUserModified(connection, null, episodeId);

    private static bool IsEpisodeUserModified(SqliteConnection connection, SqliteTransaction? transaction, long episodeId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(user_modified,0) FROM episodes WHERE id=$id";
        command.Parameters.AddWithValue("$id", episodeId);
        return Convert.ToInt32(command.ExecuteScalar() ?? 0) == 1;
    }

    private static string ReadCurrentRollbackValue(SqliteConnection connection, ResearchImportChangeRecord change)
        => ReadCurrentRollbackValue(connection, null, change);

    private static string ReadCurrentRollbackValue(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ResearchImportChangeRecord change)
    {
        if (!change.EpisodeId.HasValue) return change.AfterValue;
        var episodeId = change.EpisodeId.Value;
        switch (change.FieldName)
        {
            case "headline": return ReadEpisodeColumn(connection, transaction, episodeId, "title");
            case "summary": return ReadEpisodeColumn(connection, transaction, episodeId, "description");
            case "station": return ReadEpisodeColumn(connection, transaction, episodeId, "edition");
            case "archive_notes": return ReadEpisodeColumn(connection, transaction, episodeId, "archive_notes");
            case "hosts": return SerializeRollbackList(SplitResearchNames(ReadEpisodeColumn(connection, transaction, episodeId, "hosts")));
            case "callers": return SerializeRollbackList(SplitResearchNames(ReadEpisodeColumn(connection, transaction, episodeId, "callers")));
            case "mentioned_people": return SerializeRollbackList(SplitResearchNames(ReadEpisodeColumn(connection, transaction, episodeId, "mentioned_people")));
            case "guests": return SerializeRollbackList(ReadNames(connection, episodeId, "episode_guests", "guests", "guest_id", transaction));
            case "topics": return SerializeRollbackList(ReadNames(connection, episodeId, "episode_tags", "tags", "tag_id", transaction));
            case "sources": return SerializeRollbackList(SplitLines(ReadEpisodeColumn(connection, transaction, episodeId, "research_sources")));
            case "moments": return SerializeRollbackList(ReadEpisodeMomentKeys(connection, transaction, episodeId));
            default: return change.AfterValue;
        }
    }

    private static string ReadEpisodeColumn(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long episodeId,
        string column)
    {
        if (column is not ("title" or "description" or "edition" or "archive_notes" or "hosts" or "callers" or "mentioned_people" or "research_sources"))
            throw new ArgumentOutOfRangeException(nameof(column));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT COALESCE({column},'') FROM episodes WHERE id=$id";
        command.Parameters.AddWithValue("$id", episodeId);
        return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
    }

    private static string SerializeRollbackList(IEnumerable<string> values)
        => JsonSerializer.Serialize(NormalizeNames(values));

    private static string NormalizeRollbackComparison(string fieldName, string value)
    {
        if (fieldName is not ("hosts" or "guests" or "callers" or "mentioned_people" or "topics" or "sources" or "moments"))
            return (value ?? string.Empty).Replace("\r\n", "\n").Trim();
        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(value ?? "[]") ?? new List<string>();
            return JsonSerializer.Serialize(list
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            return JsonSerializer.Serialize(Array.Empty<string>());
        }
    }

    private static ResearchImportRollbackItem BuildResearchRecordRollbackItem(
        SqliteConnection connection,
        string snapshotPath,
        bool snapshotAvailable,
        long importRunId,
        long researchBroadcastId,
        string identity)
    {
        if (!snapshotAvailable)
        {
            return new ResearchImportRollbackItem
            {
                ResearchBroadcastId = researchBroadcastId,
                RecordIdentity = identity,
                FieldName = "research_record",
                CurrentValue = "Current durable research record",
                Status = "blocked",
                Reason = "The pre-import database snapshot is unavailable, so the durable research record cannot be restored safely."
            };
        }

        using var snapshot = new SqliteConnection($"Data Source={snapshotPath};Mode=ReadOnly");
        snapshot.Open();
        var existedBefore = ResearchRecordExists(snapshot, researchBroadcastId);
        using var current = connection.CreateCommand();
        current.CommandText = """
            SELECT import_run_id,user_modified,updated_at,
                   EXISTS(SELECT 1 FROM research_reconciliation_actions rra
                          WHERE rra.research_broadcast_id=research_broadcasts.id
                            AND rra.created_at>(SELECT imported_at FROM research_import_runs WHERE id=$run)),
                   EXISTS(SELECT 1 FROM research_quality_actions rqa
                          WHERE rqa.research_broadcast_id=research_broadcasts.id
                            AND rqa.created_at>(SELECT imported_at FROM research_import_runs WHERE id=$run)),
                   EXISTS(SELECT 1 FROM episodes e
                          WHERE e.id=research_broadcasts.episode_id AND COALESCE(e.user_modified,0)=1),
                   EXISTS(SELECT 1 FROM research_field_provenance rfp
                          WHERE rfp.research_broadcast_id=research_broadcasts.id
                            AND rfp.active=1 AND rfp.source_kind='manual'
                            AND rfp.created_at>(SELECT imported_at FROM research_import_runs WHERE id=$run))
            FROM research_broadcasts WHERE id=$id
            """;
        current.Parameters.AddWithValue("$id", researchBroadcastId);
        current.Parameters.AddWithValue("$run", importRunId);
        using var reader = current.ExecuteReader();
        if (!reader.Read())
        {
            return new ResearchImportRollbackItem
            {
                ResearchBroadcastId = researchBroadcastId,
                RecordIdentity = identity,
                FieldName = "research_record",
                BeforeValue = existedBefore ? "Pre-import durable research record" : string.Empty,
                AfterValue = "Imported durable research record",
                CurrentValue = "Research record no longer exists",
                Status = "already_reverted",
                Reason = "The durable research record is already absent."
            };
        }

        var currentRunId = reader.IsDBNull(0) ? (long?)null : reader.GetInt64(0);
        var userModified = reader.GetInt32(1) == 1;
        var hasLaterReconciliation = reader.GetInt32(3) == 1;
        var hasLaterQualityAction = reader.GetInt32(4) == 1;
        var linkedEpisodeModified = reader.GetInt32(5) == 1;
        var hasLaterManualProvenance = reader.GetInt32(6) == 1;
        var safe = currentRunId == importRunId
                   && !userModified
                   && !linkedEpisodeModified
                   && !hasLaterManualProvenance
                   && !hasLaterReconciliation
                   && !hasLaterQualityAction;
        var reason = safe
            ? existedBefore
                ? "The durable research record still belongs to this import and can be restored from the pre-import snapshot."
                : "The durable research record was created by this import and can be removed safely."
            : userModified || linkedEpisodeModified || hasLaterManualProvenance
                ? "Manual metadata or provenance now depends on this durable research record and is protected."
                : hasLaterReconciliation || hasLaterQualityAction
                    ? "Later research decisions depend on this record and are protected."
                    : "A later import now owns this durable research record.";

        return new ResearchImportRollbackItem
        {
            ResearchBroadcastId = researchBroadcastId,
            RecordIdentity = identity,
            FieldName = "research_record",
            BeforeValue = existedBefore ? "Pre-import durable research record" : string.Empty,
            AfterValue = existedBefore ? "Research record updated by import" : "Research record created by import",
            CurrentValue = existedBefore ? "Current durable research record" : "Imported durable research record",
            Status = safe ? "safe" : "blocked",
            Reason = reason
        };
    }

    private static bool ResearchRecordExists(SqliteConnection connection, long researchBroadcastId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM research_broadcasts WHERE id=$id LIMIT 1";
        command.Parameters.AddWithValue("$id", researchBroadcastId);
        return command.ExecuteScalar() is not null;
    }

    private static long BeginResearchImportRollback(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long importRunId,
        string scope,
        string identity,
        string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO research_import_rollbacks(import_run_id,scope,record_identity,created_at)
            VALUES($run,$scope,$identity,$now)
            """;
        command.Parameters.AddWithValue("$run", importRunId);
        command.Parameters.AddWithValue("$scope", scope);
        command.Parameters.AddWithValue("$identity", identity ?? string.Empty);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT last_insert_rowid()";
        return Convert.ToInt64(select.ExecuteScalar());
    }

    private static void RecordRollbackOutcome(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long rollbackId,
        long changeId,
        string outcome,
        string reason,
        string restoredValue,
        string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR IGNORE INTO research_import_rollback_changes(
                rollback_id,import_change_id,outcome,reason,restored_value,created_at)
            VALUES($rollback,$change,$outcome,$reason,$value,$now)
            """;
        command.Parameters.AddWithValue("$rollback", rollbackId);
        command.Parameters.AddWithValue("$change", changeId);
        command.Parameters.AddWithValue("$outcome", outcome);
        command.Parameters.AddWithValue("$reason", reason ?? string.Empty);
        command.Parameters.AddWithValue("$value", restoredValue ?? string.Empty);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    private static void ApplyEpisodeRollbackValue(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ResearchImportChangeRecord change)
    {
        var episodeId = change.EpisodeId!.Value;
        switch (change.FieldName)
        {
            case "headline": UpdateEpisodeRollbackScalar(connection, transaction, episodeId, "title", change.BeforeValue); break;
            case "summary": UpdateEpisodeRollbackScalar(connection, transaction, episodeId, "description", change.BeforeValue); break;
            case "station": UpdateEpisodeRollbackScalar(connection, transaction, episodeId, "edition", change.BeforeValue); break;
            case "archive_notes": UpdateEpisodeRollbackScalar(connection, transaction, episodeId, "archive_notes", change.BeforeValue); break;
            case "hosts": UpdateEpisodeRollbackScalar(connection, transaction, episodeId, "hosts", string.Join("|", DeserializeRollbackList(change.BeforeValue))); break;
            case "callers": UpdateEpisodeRollbackScalar(connection, transaction, episodeId, "callers", string.Join("|", DeserializeRollbackList(change.BeforeValue))); break;
            case "mentioned_people": UpdateEpisodeRollbackScalar(connection, transaction, episodeId, "mentioned_people", string.Join("|", DeserializeRollbackList(change.BeforeValue))); break;
            case "sources": UpdateEpisodeRollbackScalar(connection, transaction, episodeId, "research_sources", string.Join("\n", DeserializeRollbackList(change.BeforeValue))); break;
            case "guests": ReplaceNames(connection, transaction, episodeId, "guests", "episode_guests", "guest_id", DeserializeRollbackList(change.BeforeValue)); break;
            case "topics": ReplaceNames(connection, transaction, episodeId, "tags", "episode_tags", "tag_id", DeserializeRollbackList(change.BeforeValue)); break;
            case "moments": RemoveMomentsAddedByImport(connection, transaction, episodeId, change.BeforeValue, change.AfterValue); break;
            default: throw new InvalidOperationException($"Field '{change.FieldName}' does not support guarded rollback.");
        }
    }

    private static void UpdateEpisodeRollbackScalar(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long episodeId,
        string column,
        string value)
    {
        if (column is not ("title" or "description" or "edition" or "archive_notes" or "hosts" or "callers" or "mentioned_people" or "research_sources"))
            throw new ArgumentOutOfRangeException(nameof(column));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE episodes SET {column}=$value,updated_at=$now WHERE id=$id";
        command.Parameters.AddWithValue("$value", value ?? string.Empty);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", episodeId);
        command.ExecuteNonQuery();
    }

    private static IReadOnlyList<string> DeserializeRollbackList(string json)
    {
        try
        {
            return NormalizeNames(JsonSerializer.Deserialize<List<string>>(json ?? "[]") ?? new List<string>());
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static void RemoveMomentsAddedByImport(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long episodeId,
        string beforeJson,
        string afterJson)
    {
        var before = new HashSet<string>(DeserializeRollbackList(beforeJson), StringComparer.OrdinalIgnoreCase);
        var added = DeserializeRollbackList(afterJson).Where(key => !before.Contains(key)).ToList();
        foreach (var key in added)
        {
            var separator = key.IndexOf(':');
            if (separator <= 0 || !long.TryParse(key[..separator], out var seconds)) continue;
            var title = key[(separator + 1)..].Trim();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM moments WHERE episode_id=$episode AND position_ms=$position AND trim(title)=trim($title)";
            command.Parameters.AddWithValue("$episode", episodeId);
            command.Parameters.AddWithValue("$position", Math.Max(0, seconds) * 1000L);
            command.Parameters.AddWithValue("$title", title);
            command.ExecuteNonQuery();
        }
    }

    private static void RecordRollbackProvenance(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long importRunId,
        ResearchImportChangeRecord change,
        string now)
    {
        using (var supersede = connection.CreateCommand())
        {
            supersede.Transaction = transaction;
            supersede.CommandText = """
                UPDATE research_field_provenance
                   SET active=0,superseded_at=$now
                 WHERE active=1 AND import_run_id=$run AND field_name=$field
                   AND (($episode IS NOT NULL AND episode_id=$episode)
                     OR ($episode IS NULL AND research_broadcast_id=$research))
                """;
            supersede.Parameters.AddWithValue("$now", now);
            supersede.Parameters.AddWithValue("$run", importRunId);
            supersede.Parameters.AddWithValue("$field", change.FieldName);
            supersede.Parameters.AddWithValue("$episode", change.EpisodeId.HasValue ? change.EpisodeId.Value : DBNull.Value);
            supersede.Parameters.AddWithValue("$research", change.ResearchBroadcastId.HasValue ? change.ResearchBroadcastId.Value : DBNull.Value);
            supersede.ExecuteNonQuery();
        }

        var liveResearchId = change.ResearchBroadcastId;
        if (liveResearchId.HasValue)
        {
            using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM research_broadcasts WHERE id=$id LIMIT 1";
            exists.Parameters.AddWithValue("$id", liveResearchId.Value);
            if (exists.ExecuteScalar() is null) liveResearchId = null;
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO research_field_provenance(
                research_broadcast_id,episode_id,field_name,value_text,source_kind,source_label,
                import_run_id,confidence,evidence_count,protected,active,created_at)
            SELECT $research,$episode,$field,$value,'rollback',
                   'Restored before ' || package_name,NULL,0,0,0,1,$now
              FROM research_import_runs WHERE id=$run
            """;
        insert.Parameters.AddWithValue("$research", liveResearchId.HasValue ? liveResearchId.Value : DBNull.Value);
        insert.Parameters.AddWithValue("$episode", change.EpisodeId.HasValue ? change.EpisodeId.Value : DBNull.Value);
        insert.Parameters.AddWithValue("$field", change.FieldName);
        insert.Parameters.AddWithValue("$value", change.BeforeValue ?? string.Empty);
        insert.Parameters.AddWithValue("$now", now);
        insert.Parameters.AddWithValue("$run", importRunId);
        insert.ExecuteNonQuery();
    }

    private static void RestoreResearchRecordFromSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long importRunId,
        long researchBroadcastId)
    {
        var existedBefore = false;
        using (var exists = connection.CreateCommand())
        {
            exists.Transaction = transaction;
            exists.CommandText = "SELECT 1 FROM rollback_snapshot.research_broadcasts WHERE id=$id LIMIT 1";
            exists.Parameters.AddWithValue("$id", researchBroadcastId);
            existedBefore = exists.ExecuteScalar() is not null;
        }

        long? currentLegacyId;
        using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandText = "SELECT legacy_missing_research_id FROM research_broadcasts WHERE id=$id";
            current.Parameters.AddWithValue("$id", researchBroadcastId);
            var value = current.ExecuteScalar();
            currentLegacyId = value is null || value == DBNull.Value ? null : Convert.ToInt64(value);
        }

        if (!existedBefore)
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM research_broadcasts WHERE id=$id AND import_run_id=$run AND user_modified=0";
            delete.Parameters.AddWithValue("$id", researchBroadcastId);
            delete.Parameters.AddWithValue("$run", importRunId);
            delete.ExecuteNonQuery();
            if (currentLegacyId.HasValue && !SnapshotMissingResearchExists(connection, transaction, currentLegacyId.Value))
            {
                using var deleteLegacy = connection.CreateCommand();
                deleteLegacy.Transaction = transaction;
                deleteLegacy.CommandText = "DELETE FROM missing_broadcast_research WHERE id=$id";
                deleteLegacy.Parameters.AddWithValue("$id", currentLegacyId.Value);
                deleteLegacy.ExecuteNonQuery();
            }
            return;
        }

        // Child rows are restored from the snapshot after deleting only rows owned
        // by this research record. The preview blocks records with later actions.
        foreach (var table in new[]
                 {
                     "research_people", "research_topics", "research_moments", "research_aliases",
                     "research_conflicts", "research_reconciliation_actions", "research_reconciliation_candidates",
                     "research_field_provenance", "research_sources"
                 })
        {
            using var delete = connection.CreateCommand();
            delete.Transaction = transaction;
            delete.CommandText = $"DELETE FROM {table} WHERE research_broadcast_id=$id";
            delete.Parameters.AddWithValue("$id", researchBroadcastId);
            delete.ExecuteNonQuery();
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE research_broadcasts SET
                    identity_key=(SELECT identity_key FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    collection_id=(SELECT collection_id FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    episode_id=(SELECT episode_id FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    legacy_missing_research_id=(SELECT legacy_missing_research_id FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    source_broadcast_id=(SELECT source_broadcast_id FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    air_date=(SELECT air_date FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    slot=(SELECT slot FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    part_number=(SELECT part_number FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    total_parts=(SELECT total_parts FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    capture_key=(SELECT capture_key FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    headline=(SELECT headline FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    summary=(SELECT summary FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    station=(SELECT station FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    edition=(SELECT edition FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    broadcast_variant=(SELECT broadcast_variant FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    broadcast_era=(SELECT broadcast_era FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    episode_type=(SELECT episode_type FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    archive_notes=(SELECT archive_notes FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    research_json=(SELECT research_json FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    research_state=(SELECT research_state FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    existence_status=(SELECT existence_status FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    confidence=(SELECT confidence FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    confidence_reason=(SELECT confidence_reason FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    user_modified=(SELECT user_modified FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    needs_review=(SELECT needs_review FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    import_run_id=(SELECT import_run_id FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    attached_at=(SELECT attached_at FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    created_at=(SELECT created_at FROM rollback_snapshot.research_broadcasts WHERE id=$id),
                    updated_at=(SELECT updated_at FROM rollback_snapshot.research_broadcasts WHERE id=$id)
                WHERE id=$id
                """;
            update.Parameters.AddWithValue("$id", researchBroadcastId);
            update.ExecuteNonQuery();
        }

        CopyResearchSnapshotChildren(connection, transaction, "research_sources",
            "id,research_broadcast_id,url,title,publisher,source_type,accessed_at,confidence,supports,notes,created_at", researchBroadcastId);
        CopyResearchSnapshotChildren(connection, transaction, "research_people",
            "id,research_broadcast_id,name,role,confidence,source_id,notes,created_at", researchBroadcastId);
        CopyResearchSnapshotChildren(connection, transaction, "research_topics",
            "id,research_broadcast_id,topic,confidence,source_id,notes,created_at", researchBroadcastId);
        CopyResearchSnapshotChildren(connection, transaction, "research_moments",
            "id,research_broadcast_id,timestamp_seconds,title,description,tags,confidence,source_id,created_at", researchBroadcastId);
        CopyResearchSnapshotChildren(connection, transaction, "research_aliases",
            "id,research_broadcast_id,alias_type,alias_value,confidence", researchBroadcastId);
        CopyResearchSnapshotChildren(connection, transaction, "research_conflicts",
            "id,research_broadcast_id,episode_id,field_name,existing_value,incoming_value,existing_source,incoming_source,resolution,created_at,resolved_at", researchBroadcastId);
        CopyResearchSnapshotChildren(connection, transaction, "research_reconciliation_candidates",
            "id,research_broadcast_id,episode_id,score,reason,status,created_at,updated_at", researchBroadcastId);
        CopyResearchSnapshotChildren(connection, transaction, "research_reconciliation_actions",
            "id,candidate_id,research_broadcast_id,episode_id,action,options_json,change_json,created_at,undone_at", researchBroadcastId);
        CopyResearchSnapshotChildren(connection, transaction, "research_field_provenance",
            "id,research_broadcast_id,episode_id,field_name,value_text,source_kind,source_label,import_run_id,confidence,evidence_count,protected,active,created_at,superseded_at", researchBroadcastId);

        using var legacy = connection.CreateCommand();
        legacy.Transaction = transaction;
        legacy.CommandText = "SELECT legacy_missing_research_id FROM rollback_snapshot.research_broadcasts WHERE id=$id";
        legacy.Parameters.AddWithValue("$id", researchBroadcastId);
        var legacyValue = legacy.ExecuteScalar();
        if (legacyValue is not null && legacyValue != DBNull.Value)
            RestoreLegacyMissingResearchFromSnapshot(connection, transaction, Convert.ToInt64(legacyValue));
    }

    private static bool SnapshotMissingResearchExists(SqliteConnection connection, SqliteTransaction transaction, long legacyId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT 1 FROM rollback_snapshot.missing_broadcast_research WHERE id=$id LIMIT 1";
        command.Parameters.AddWithValue("$id", legacyId);
        return command.ExecuteScalar() is not null;
    }

    private static void CopyResearchSnapshotChildren(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string table,
        string columns,
        long researchBroadcastId)
    {
        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "SELECT 1 FROM rollback_snapshot.sqlite_master WHERE type='table' AND name=$table LIMIT 1";
            check.Parameters.AddWithValue("$table", table);
            if (check.ExecuteScalar() is null) return;
        }

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT INTO {table}({columns}) SELECT {columns} FROM rollback_snapshot.{table} WHERE research_broadcast_id=$id";
        command.Parameters.AddWithValue("$id", researchBroadcastId);
        command.ExecuteNonQuery();
    }

    private static void RestoreLegacyMissingResearchFromSnapshot(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long legacyId)
    {
        if (!SnapshotMissingResearchExists(connection, transaction, legacyId)) return;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO missing_broadcast_research(
                id,stable_key,broadcast_uid,show_name,normalized_show_name,broadcast_date,slot,
                normalized_slot,part_number,total_parts,headline,summary,confidence,confidence_reason,
                research_json,status,matched_episode_id,match_notes,created_at,updated_at,resolved_at)
            SELECT id,stable_key,broadcast_uid,show_name,normalized_show_name,broadcast_date,slot,
                   normalized_slot,part_number,total_parts,headline,summary,confidence,confidence_reason,
                   research_json,status,matched_episode_id,match_notes,created_at,updated_at,resolved_at
              FROM rollback_snapshot.missing_broadcast_research WHERE id=$id
            ON CONFLICT(id) DO UPDATE SET
                stable_key=excluded.stable_key,broadcast_uid=excluded.broadcast_uid,
                show_name=excluded.show_name,normalized_show_name=excluded.normalized_show_name,
                broadcast_date=excluded.broadcast_date,slot=excluded.slot,normalized_slot=excluded.normalized_slot,
                part_number=excluded.part_number,total_parts=excluded.total_parts,headline=excluded.headline,
                summary=excluded.summary,confidence=excluded.confidence,confidence_reason=excluded.confidence_reason,
                research_json=excluded.research_json,status=excluded.status,matched_episode_id=excluded.matched_episode_id,
                match_notes=excluded.match_notes,created_at=excluded.created_at,updated_at=excluded.updated_at,
                resolved_at=excluded.resolved_at
            """;
        command.Parameters.AddWithValue("$id", legacyId);
        command.ExecuteNonQuery();
    }

    private static void CompleteResearchImportRollback(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long importRunId,
        long rollbackId,
        ResearchImportRollbackResult result,
        ResearchImportRollbackPreview preview,
        string now)
    {
        var summaryJson = JsonSerializer.Serialize(new
        {
            preview.PackageName,
            Scope = preview.IsSingleBroadcast ? "record" : "entire_import",
            preview.TargetIdentity,
            result.Applied,
            result.Blocked,
            result.AlreadyRestored,
            result.Preserved,
            result.Partial
        });
        using (var rollback = connection.CreateCommand())
        {
            rollback.Transaction = transaction;
            rollback.CommandText = """
                UPDATE research_import_rollbacks
                   SET restored_count=$restored,blocked_count=$blocked,summary_json=$summary
                 WHERE id=$id
                """;
            rollback.Parameters.AddWithValue("$restored", result.Applied);
            rollback.Parameters.AddWithValue("$blocked", result.Blocked);
            rollback.Parameters.AddWithValue("$summary", summaryJson);
            rollback.Parameters.AddWithValue("$id", rollbackId);
            rollback.ExecuteNonQuery();
        }

        var status = preview.IsSingleBroadcast || result.Blocked > 0
            ? "partially_rolled_back"
            : "rolled_back";
        using var run = connection.CreateCommand();
        run.Transaction = transaction;
        run.CommandText = """
            UPDATE research_import_runs SET
                status=$status,
                restored_change_count=restored_change_count+$restored,
                blocked_rollback_count=blocked_rollback_count+$blocked,
                last_rollback_at=$now
            WHERE id=$id
            """;
        run.Parameters.AddWithValue("$status", status);
        run.Parameters.AddWithValue("$restored", result.Applied);
        run.Parameters.AddWithValue("$blocked", result.Blocked);
        run.Parameters.AddWithValue("$now", now);
        run.Parameters.AddWithValue("$id", importRunId);
        run.ExecuteNonQuery();
    }

    private static void RecordDurableResearchImportProvenance(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long importRunId,
        long researchBroadcastId,
        TrvPackBroadcast item)
    {
        item.Research ??= new TrvPackResearch();
        item.Research.Broadcast ??= new TrvPackBroadcastMetadata();
        item.Research.People ??= new TrvPackPeople();
        item.Research.Topics ??= new List<string>();
        item.Research.Guests ??= new List<string>();
        item.Sources ??= new List<TrvPackSource>();

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["headline"] = item.Research.Headline?.Trim() ?? string.Empty,
            ["summary"] = item.Research.Summary?.Trim() ?? string.Empty,
            ["station"] = item.Research.Broadcast.Station?.Trim() ?? item.Research.Edition?.Trim() ?? string.Empty,
            ["archive_notes"] = item.Research.ArchiveNotes?.Trim() ?? string.Empty,
            ["hosts"] = JsonSerializer.Serialize(NormalizeNames(item.Research.People.Hosts ?? new List<string>())),
            ["guests"] = JsonSerializer.Serialize(NormalizeNames((item.Research.People.Guests ?? new List<string>()).Concat(item.Research.Guests))),
            ["callers"] = JsonSerializer.Serialize(NormalizeNames(item.Research.People.Callers ?? new List<string>())),
            ["mentioned_people"] = JsonSerializer.Serialize(NormalizeNames(item.Research.People.MentionedPeople ?? new List<string>())),
            ["topics"] = JsonSerializer.Serialize(NormalizeNames(item.Research.Topics)),
            ["sources"] = JsonSerializer.Serialize(NormalizeNames(item.Sources.Select(source => source.Url)))
        };

        foreach (var pair in fields)
        {
            if (string.IsNullOrWhiteSpace(pair.Value) || pair.Value == "[]") continue;
            RecordImportFieldProvenance(connection, transaction, importRunId, researchBroadcastId, null,
                pair.Key, pair.Value, protectedValue: false);
        }
    }

}
