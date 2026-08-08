using Microsoft.Data.Sqlite;
using System.Text.Json;
using TheRadioVault.Models;
using TheRadioVault.Research.Services;

namespace TheRadioVault.Services;

public sealed partial class DatabaseService
{
    public IReadOnlyList<ResearchQualityActionRecord> GetResearchQualityActions(int limit = 50)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.id,a.research_broadcast_id,a.episode_id,a.rule_id,a.fix_kind,a.created_at,a.undone_at,
                   COALESCE(c.name,''),r.air_date,COALESCE(r.headline,'')
            FROM research_quality_actions a
            LEFT JOIN research_broadcasts r ON r.id=a.research_broadcast_id
            LEFT JOIN collections c ON c.id=r.collection_id
            ORDER BY a.created_at DESC,a.id DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));
        using var reader = command.ExecuteReader();
        var rows = new List<ResearchQualityActionRecord>();
        while (reader.Read())
        {
            rows.Add(new ResearchQualityActionRecord
            {
                ActionId = reader.GetInt64(0),
                ResearchBroadcastId = reader.GetInt64(1),
                EpisodeId = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                RuleId = reader.GetString(3),
                FixKind = reader.GetString(4),
                CreatedAt = DateTime.Parse(reader.GetString(5)),
                UndoneAt = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6)),
                Show = reader.GetString(7),
                BroadcastDate = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8)),
                Headline = reader.GetString(9)
            });
        }
        return rows;
    }

    public HashSet<string> GetActiveResearchQualityDecisionKeys()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT research_broadcast_id,rule_id,fix_value
              FROM research_quality_actions
             WHERE undone_at IS NULL
               AND fix_kind IN ('DecisionKeepMultipleRoles','DecisionKeepGenericSummary','DecisionKeepWeakTopic')
            """;
        using var reader = command.ExecuteReader();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        while (reader.Read())
            keys.Add($"{reader.GetInt64(0)}|{reader.GetString(1)}|{reader.GetString(2)}");
        return keys;
    }

    public ResearchQualityRepairResult ApplyResearchQualityDecision(ResearchQualityFinding finding, string optionKey)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (!finding.SupportsDirectDecision)
            return new ResearchQualityRepairResult { Summary = "This issue does not have a direct decision in this build." };

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var before = ReadResearchRepairSnapshot(connection, finding.ResearchBroadcastId, finding.EpisodeId, transaction);
        if (before is null)
            return new ResearchQualityRepairResult { Summary = "The affected research record no longer exists." };

        var changed = false;
        string fixKind;
        string summary;
        switch (finding.DirectDecisionKind)
        {
            case "person-role" when optionKey.Equals("keep-all", StringComparison.OrdinalIgnoreCase):
                fixKind = "DecisionKeepMultipleRoles";
                summary = $"Kept all recorded roles for {finding.DirectDecisionSubject}.";
                break;
            case "person-role" when optionKey.StartsWith("role:", StringComparison.OrdinalIgnoreCase):
                var selectedRole = optionKey[5..].Trim().ToLowerInvariant();
                changed = ApplySelectPersonRole(connection, transaction, finding, before, selectedRole);
                if (!changed)
                {
                    transaction.Rollback();
                    return new ResearchQualityRepairResult { Summary = "The role assignment changed before this choice could be applied. Recheck the issue." };
                }
                fixKind = "DecisionSelectPersonRole";
                summary = $"Stored {finding.DirectDecisionSubject} as {ResearchRoleDisplay(selectedRole)} only.";
                break;
            case "generic-summary" when optionKey.Equals("keep", StringComparison.OrdinalIgnoreCase):
                fixKind = "DecisionKeepGenericSummary";
                summary = "Kept this summary as an intentional description.";
                break;
            case "weak-topic" when optionKey.Equals("keep", StringComparison.OrdinalIgnoreCase):
                fixKind = "DecisionKeepWeakTopic";
                summary = $"Kept “{finding.DirectDecisionSubject}” as an intentional topic.";
                break;
            case "weak-topic" when optionKey.Equals("remove", StringComparison.OrdinalIgnoreCase):
                changed = ApplyRemoveWeakTopic(connection, transaction, finding, before);
                if (!changed)
                {
                    transaction.Rollback();
                    return new ResearchQualityRepairResult { Summary = "The topic changed before this choice could be applied. Recheck the issue." };
                }
                fixKind = "DecisionRemoveWeakTopic";
                summary = $"Removed the broad topic “{finding.DirectDecisionSubject}”.";
                break;
            default:
                transaction.Rollback();
                return new ResearchQualityRepairResult { Summary = "That choice is no longer valid for this issue." };
        }

        if (changed) TouchResearchRecord(connection, transaction, finding.ResearchBroadcastId);
        var after = ReadResearchRepairSnapshot(connection, finding.ResearchBroadcastId, finding.EpisodeId, transaction)
            ?? throw new InvalidOperationException("The updated research record could not be reloaded.");
        var actionId = InsertResearchQualityAction(connection, transaction, finding, before, after, fixKind, finding.DirectDecisionFingerprint);
        transaction.Commit();
        return new ResearchQualityRepairResult { Applied = true, ActionId = actionId, Summary = summary };
    }

    private static readonly JsonSerializerOptions ResearchRepairJsonOptions = new(JsonSerializerDefaults.Web);

    public ResearchQualityRepairPreview PreviewResearchQualityRepair(ResearchQualityFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        if (!finding.SafeFixAvailable)
            return new ResearchQualityRepairPreview { CanApply = false, Title = finding.Title, Warning = "This finding requires manual review." };

        using var connection = OpenConnection();
        var snapshot = ReadResearchRepairSnapshot(connection, finding.ResearchBroadcastId, finding.EpisodeId);
        if (snapshot is null)
            return new ResearchQualityRepairPreview { CanApply = false, Title = finding.Title, Warning = "The research record no longer exists." };

        return finding.SafeFixKind switch
        {
            "RemoveGenericGuest" => PreviewGenericGuest(finding, snapshot),
            "RemoveDuplicateTopic" => PreviewDuplicateTopic(finding, snapshot),
            "NormalisePersonName" => PreviewPersonName(finding, snapshot),
            "RemoveDuplicateSource" => PreviewDuplicateSource(finding, snapshot),
            "ClearGenericHeadline" => PreviewGenericHeadline(finding, snapshot),
            _ => new ResearchQualityRepairPreview { CanApply = false, Title = finding.Title, Warning = "This repair type is not supported by this build." }
        };
    }

    public ResearchQualityRepairResult ApplyResearchQualityRepair(ResearchQualityFinding finding)
    {
        ArgumentNullException.ThrowIfNull(finding);
        var preview = PreviewResearchQualityRepair(finding);
        if (!preview.CanApply)
            return new ResearchQualityRepairResult { Summary = preview.Warning };

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var before = ReadResearchRepairSnapshot(connection, finding.ResearchBroadcastId, finding.EpisodeId, transaction)
            ?? throw new InvalidOperationException("The research record no longer exists.");
        var changed = finding.SafeFixKind switch
        {
            "RemoveGenericGuest" => ApplyRemoveGenericGuest(connection, transaction, finding, before),
            "RemoveDuplicateTopic" => ApplyRemoveDuplicateTopic(connection, transaction, finding, before),
            "NormalisePersonName" => ApplyNormalisePersonName(connection, transaction, finding, before),
            "RemoveDuplicateSource" => ApplyRemoveDuplicateSource(connection, transaction, finding, before),
            "ClearGenericHeadline" => ApplyClearGenericHeadline(connection, transaction, finding, before),
            _ => false
        };

        if (!changed)
        {
            transaction.Rollback();
            return new ResearchQualityRepairResult { Summary = "Nothing changed because the record no longer matches the audit finding." };
        }

        TouchResearchRecord(connection, transaction, finding.ResearchBroadcastId);
        var after = ReadResearchRepairSnapshot(connection, finding.ResearchBroadcastId, finding.EpisodeId, transaction)
            ?? throw new InvalidOperationException("The repaired research record could not be reloaded.");
        var actionId = InsertResearchQualityAction(connection, transaction, finding, before, after);
        transaction.Commit();
        return new ResearchQualityRepairResult { Applied = true, ActionId = actionId, Summary = preview.After };
    }

    public ResearchQualityUndoResult UndoLastResearchQualityRepair()
    {
        long? researchBroadcastId;
        using (var connection = OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT research_broadcast_id FROM research_quality_actions WHERE undone_at IS NULL ORDER BY id DESC LIMIT 1";
            var value = command.ExecuteScalar();
            researchBroadcastId = value is null || value is DBNull ? null : Convert.ToInt64(value);
        }

        return researchBroadcastId.HasValue
            ? UndoLastResearchQualityRepair(researchBroadcastId.Value)
            : new ResearchQualityUndoResult { Summary = "There is no active safe repair to undo." };
    }

    public ResearchQualityUndoResult UndoLastResearchQualityRepair(long researchBroadcastId)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,episode_id,before_json,after_json
              FROM research_quality_actions
             WHERE research_broadcast_id=$research AND undone_at IS NULL
             ORDER BY id DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$research", researchBroadcastId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return new ResearchQualityUndoResult { Summary = "There is no active safe repair to undo for this record." };
        var actionId = reader.GetInt64(0);
        long? episodeId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        var beforeJson = reader.GetString(2);
        var afterJson = reader.GetString(3);
        reader.Close();

        var current = ReadResearchRepairSnapshot(connection, researchBroadcastId, episodeId, transaction);
        if (current is null)
            return new ResearchQualityUndoResult { Summary = "The research record no longer exists." };
        var currentJson = JsonSerializer.Serialize(current, ResearchRepairJsonOptions);
        if (!ResearchRepairGuard.CanUndo(currentJson, afterJson))
        {
            transaction.Rollback();
            return new ResearchQualityUndoResult
            {
                RefusedBecauseChanged = true,
                Summary = "Undo was refused because this record changed after the repair. This protects later manual edits and imports."
            };
        }

        var before = JsonSerializer.Deserialize<ResearchRepairSnapshot>(beforeJson, ResearchRepairJsonOptions)
            ?? throw new InvalidDataException("The saved repair snapshot is invalid.");
        RestoreResearchRepairSnapshot(connection, transaction, before);
        using (var mark = connection.CreateCommand())
        {
            mark.Transaction = transaction;
            mark.CommandText = "UPDATE research_quality_actions SET undone_at=$now WHERE id=$id";
            mark.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            mark.Parameters.AddWithValue("$id", actionId);
            mark.ExecuteNonQuery();
        }
        transaction.Commit();
        return new ResearchQualityUndoResult { Undone = true, Summary = "The last safe repair was undone." };
    }

    private static ResearchQualityRepairPreview PreviewGenericGuest(ResearchQualityFinding finding, ResearchRepairSnapshot snapshot)
    {
        var count = snapshot.ResearchPeople.Count(x => x.Role.Equals("guest", StringComparison.OrdinalIgnoreCase) && SameText(x.Name, finding.SafeFixValue));
        var episodeCount = snapshot.EpisodeGuests.Count(x => SameText(x, finding.SafeFixValue));
        return new ResearchQualityRepairPreview
        {
            CanApply = count + episodeCount > 0,
            Title = "Remove generic guest entry",
            Before = $"“{finding.SafeFixValue}” is stored as a guest.",
            After = "The generic show-name guest entry will be removed from research and attached episode metadata.",
            Warning = count + episodeCount > 0 ? "Named guests and all other people roles will be preserved." : "The guest entry has already been removed."
        };
    }

    private static ResearchQualityRepairPreview PreviewDuplicateTopic(ResearchQualityFinding finding, ResearchRepairSnapshot snapshot)
    {
        var research = snapshot.ResearchTopics.Count(x => SameNormalised(x.Topic, finding.SafeFixValue));
        var episode = snapshot.EpisodeTags.Count(x => SameNormalised(x, finding.SafeFixValue));
        return new ResearchQualityRepairPreview
        {
            CanApply = research > 1 || episode > 1,
            Title = "Merge duplicate topic",
            Before = $"The normalised topic “{finding.SafeFixValue}” appears {Math.Max(research, episode):N0} times.",
            After = "One canonical topic will be retained and duplicates removed.",
            Warning = research > 1 || episode > 1 ? "Topic confidence and notes from the retained research row will be preserved." : "The duplicate topic has already been resolved."
        };
    }

    private static ResearchQualityRepairPreview PreviewPersonName(ResearchQualityFinding finding, ResearchRepairSnapshot snapshot)
    {
        var key = NormalisePersonKey(finding.SafeFixValue);
        var variants = snapshot.ResearchPeople.Select(x => x.Name)
            .Concat(snapshot.EpisodeGuests)
            .Concat(SplitPipeValues(snapshot.EpisodeHosts))
            .Concat(SplitPipeValues(snapshot.EpisodeCallers))
            .Concat(SplitPipeValues(snapshot.EpisodeMentionedPeople))
            .Where(x => NormalisePersonKey(x) == key)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return new ResearchQualityRepairPreview
        {
            CanApply = variants.Length > 1,
            Title = "Normalise person name",
            Before = variants.Length > 0 ? string.Join(", ", variants.Select(x => $"“{x}”")) : finding.SafeFixValue,
            After = $"Matching variants will use the canonical display name “{finding.SafeFixValue.Trim()}”.",
            Warning = variants.Length > 1 ? "Roles and source links will be preserved; exact duplicate role rows will be merged." : "The name variants have already been normalised."
        };
    }

    private static ResearchQualityRepairPreview PreviewDuplicateSource(ResearchQualityFinding finding, ResearchRepairSnapshot snapshot)
    {
        var matches = snapshot.ResearchSources.Count(x => SameSourceUrl(x.Url, finding.SafeFixValue));
        return new ResearchQualityRepairPreview
        {
            CanApply = matches > 1,
            Title = "Merge duplicate source link",
            Before = $"The same source URL is stored {matches:N0} times.",
            After = "The strongest source row will be retained and child references moved to it.",
            Warning = matches > 1 ? "The highest-confidence source is kept; references from people, topics and moments are preserved." : "The duplicate source has already been resolved."
        };
    }

    private static ResearchQualityRepairPreview PreviewGenericHeadline(ResearchQualityFinding finding, ResearchRepairSnapshot snapshot)
    {
        var researchMatch = SameText(snapshot.ResearchHeadline, finding.SafeFixValue);
        var protectedResearch = snapshot.ResearchUserModified;
        var protectedEpisode = snapshot.EpisodeUserModified;
        return new ResearchQualityRepairPreview
        {
            CanApply = researchMatch && !protectedResearch,
            Title = "Clear generic headline",
            Before = $"“{snapshot.ResearchHeadline}” is stored as the research headline.",
            After = "The generic research headline will be cleared so a specific title can be added later.",
            Warning = protectedResearch
                ? "The research record was manually edited, so this headline cannot be cleared automatically."
                : protectedEpisode
                    ? "The attached episode was manually edited, so its title will not be changed."
                    : "A matching automatically generated episode title will also be cleared; manual titles are always protected."
        };
    }

    private static bool ApplyRemoveGenericGuest(SqliteConnection connection, SqliteTransaction transaction, ResearchQualityFinding finding, ResearchRepairSnapshot snapshot)
    {
        var changed = Execute(connection, transaction,
            "DELETE FROM research_people WHERE research_broadcast_id=$research AND role='guest' AND lower(trim(name))=lower(trim($value))",
            ("$research", finding.ResearchBroadcastId), ("$value", finding.SafeFixValue)) > 0;
        if (snapshot.EpisodeId.HasValue)
        {
            changed |= Execute(connection, transaction, """
                DELETE FROM episode_guests
                 WHERE episode_id=$episode
                   AND guest_id IN (SELECT id FROM guests WHERE lower(trim(name))=lower(trim($value)))
                """, ("$episode", snapshot.EpisodeId.Value), ("$value", finding.SafeFixValue)) > 0;
        }
        return changed;
    }

    private static bool ApplyRemoveDuplicateTopic(SqliteConnection connection, SqliteTransaction transaction, ResearchQualityFinding finding, ResearchRepairSnapshot snapshot)
    {
        var matching = snapshot.ResearchTopics.Where(x => SameNormalised(x.Topic, finding.SafeFixValue)).OrderBy(x => x.Id).ToArray();
        var changed = false;
        foreach (var duplicate in matching.Skip(1))
            changed |= Execute(connection, transaction, "DELETE FROM research_topics WHERE id=$id", ("$id", duplicate.Id)) > 0;

        if (snapshot.EpisodeId.HasValue)
        {
            var tagIds = new List<long>();
            using var get = connection.CreateCommand();
            get.Transaction = transaction;
            get.CommandText = """
                SELECT t.id,t.name FROM episode_tags et JOIN tags t ON t.id=et.tag_id WHERE et.episode_id=$episode ORDER BY t.id
                """;
            get.Parameters.AddWithValue("$episode", snapshot.EpisodeId.Value);
            using var reader = get.ExecuteReader();
            while (reader.Read())
                if (SameNormalised(reader.GetString(1), finding.SafeFixValue)) tagIds.Add(reader.GetInt64(0));
            reader.Close();
            foreach (var tagId in tagIds.Skip(1))
                changed |= Execute(connection, transaction, "DELETE FROM episode_tags WHERE episode_id=$episode AND tag_id=$tag", ("$episode", snapshot.EpisodeId.Value), ("$tag", tagId)) > 0;
        }
        return changed;
    }

    private static bool ApplyNormalisePersonName(SqliteConnection connection, SqliteTransaction transaction, ResearchQualityFinding finding, ResearchRepairSnapshot snapshot)
    {
        var canonical = finding.SafeFixValue.Trim();
        var key = NormalisePersonKey(canonical);
        var matches = snapshot.ResearchPeople.Where(x => NormalisePersonKey(x.Name) == key).ToArray();
        var changed = false;
        foreach (var roleGroup in matches.GroupBy(x => x.Role, StringComparer.OrdinalIgnoreCase))
        {
            var keep = roleGroup.OrderByDescending(x => SameText(x.Name, canonical)).ThenByDescending(x => x.Confidence).ThenBy(x => x.Id).First();
            foreach (var duplicate in roleGroup.Where(x => x.Id != keep.Id))
                changed |= Execute(connection, transaction, "DELETE FROM research_people WHERE id=$id", ("$id", duplicate.Id)) > 0;
            if (!string.Equals(keep.Name, canonical, StringComparison.Ordinal))
                changed |= Execute(connection, transaction, "UPDATE research_people SET name=$name WHERE id=$id", ("$name", canonical), ("$id", keep.Id)) > 0;
        }

        if (snapshot.EpisodeId.HasValue)
        {
            var episodeId = snapshot.EpisodeId.Value;
            changed |= NormaliseEpisodePipeField(connection, transaction, episodeId, "hosts", snapshot.EpisodeHosts, key, canonical);
            changed |= NormaliseEpisodePipeField(connection, transaction, episodeId, "callers", snapshot.EpisodeCallers, key, canonical);
            changed |= NormaliseEpisodePipeField(connection, transaction, episodeId, "mentioned_people", snapshot.EpisodeMentionedPeople, key, canonical);
            var guestMatches = snapshot.EpisodeGuests.Where(x => NormalisePersonKey(x) == key).ToArray();
            if (guestMatches.Length > 0)
            {
                Execute(connection, transaction, "INSERT OR IGNORE INTO guests(name) VALUES($name)", ("$name", canonical));
                long canonicalId;
                using (var id = connection.CreateCommand())
                {
                    id.Transaction = transaction;
                    id.CommandText = "SELECT id FROM guests WHERE name=$name COLLATE NOCASE ORDER BY CASE WHEN name=$name THEN 0 ELSE 1 END,id LIMIT 1";
                    id.Parameters.AddWithValue("$name", canonical);
                    canonicalId = Convert.ToInt64(id.ExecuteScalar());
                }
                foreach (var variant in guestMatches)
                    changed |= Execute(connection, transaction, "DELETE FROM episode_guests WHERE episode_id=$episode AND guest_id IN(SELECT id FROM guests WHERE lower(trim(name))=lower(trim($name)))", ("$episode", episodeId), ("$name", variant)) > 0;
                Execute(connection, transaction, "INSERT OR IGNORE INTO episode_guests(episode_id,guest_id) VALUES($episode,$guest)", ("$episode", episodeId), ("$guest", canonicalId));
                changed = true;
            }
        }
        return changed;
    }

    private static bool ApplyRemoveDuplicateSource(SqliteConnection connection, SqliteTransaction transaction, ResearchQualityFinding finding, ResearchRepairSnapshot snapshot)
    {
        var matches = snapshot.ResearchSources.Where(x => SameSourceUrl(x.Url, finding.SafeFixValue))
            .OrderByDescending(x => x.Confidence).ThenByDescending(x => !string.IsNullOrWhiteSpace(x.Notes)).ThenBy(x => x.Id).ToArray();
        if (matches.Length <= 1) return false;
        var keep = matches[0];
        foreach (var duplicate in matches.Skip(1))
        {
            Execute(connection, transaction, "UPDATE research_people SET source_id=$keep WHERE source_id=$duplicate", ("$keep", keep.Id), ("$duplicate", duplicate.Id));
            Execute(connection, transaction, "UPDATE research_topics SET source_id=$keep WHERE source_id=$duplicate", ("$keep", keep.Id), ("$duplicate", duplicate.Id));
            Execute(connection, transaction, "UPDATE research_moments SET source_id=$keep WHERE source_id=$duplicate", ("$keep", keep.Id), ("$duplicate", duplicate.Id));
            Execute(connection, transaction, "DELETE FROM research_sources WHERE id=$id", ("$id", duplicate.Id));
        }
        return true;
    }

    private static bool ApplyClearGenericHeadline(SqliteConnection connection, SqliteTransaction transaction, ResearchQualityFinding finding, ResearchRepairSnapshot snapshot)
    {
        if (snapshot.ResearchUserModified) return false;
        var changed = Execute(connection, transaction,
            "UPDATE research_broadcasts SET headline='',updated_at=$now WHERE id=$id AND COALESCE(user_modified,0)=0 AND lower(trim(headline))=lower(trim($value))",
            ("$now", DateTime.UtcNow.ToString("O")), ("$id", finding.ResearchBroadcastId), ("$value", finding.SafeFixValue)) > 0;
        if (snapshot.EpisodeId.HasValue && !snapshot.EpisodeUserModified && SameText(snapshot.EpisodeTitle, finding.SafeFixValue))
        {
            changed |= Execute(connection, transaction,
                "UPDATE episodes SET title='',updated_at=$now WHERE id=$id AND COALESCE(user_modified,0)=0 AND lower(trim(title))=lower(trim($value))",
                ("$now", DateTime.UtcNow.ToString("O")), ("$id", snapshot.EpisodeId.Value), ("$value", finding.SafeFixValue)) > 0;
        }
        return changed;
    }

    private static bool ApplySelectPersonRole(SqliteConnection connection, SqliteTransaction transaction, ResearchQualityFinding finding, ResearchRepairSnapshot snapshot, string selectedRole)
    {
        if (selectedRole is not ("host" or "guest" or "caller" or "mentioned")) return false;
        var person = finding.DirectDecisionSubject.Trim();
        var key = NormalisePersonKey(person);
        var matches = snapshot.ResearchPeople.Where(x => NormalisePersonKey(x.Name) == key).ToArray();
        var selected = matches.Where(x => x.Role.Equals(selectedRole, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.Confidence).ThenBy(x => x.Id).FirstOrDefault();
        if (selected is null) return false;

        var changed = false;
        foreach (var row in matches.Where(x => x.Id != selected.Id))
            changed |= Execute(connection, transaction, "DELETE FROM research_people WHERE id=$id", ("$id", row.Id)) > 0;
        changed |= Execute(connection, transaction, "UPDATE research_broadcasts SET user_modified=1,updated_at=$now WHERE id=$id",
            ("$now", DateTime.UtcNow.ToString("O")), ("$id", finding.ResearchBroadcastId)) > 0;

        if (!snapshot.EpisodeId.HasValue) return changed;
        var episodeId = snapshot.EpisodeId.Value;
        var hosts = SplitPipeValues(snapshot.EpisodeHosts).Where(x => NormalisePersonKey(x) != key).ToList();
        var guests = snapshot.EpisodeGuests.Where(x => NormalisePersonKey(x) != key).ToList();
        var callers = SplitPipeValues(snapshot.EpisodeCallers).Where(x => NormalisePersonKey(x) != key).ToList();
        var mentioned = SplitPipeValues(snapshot.EpisodeMentionedPeople).Where(x => NormalisePersonKey(x) != key).ToList();
        switch (selectedRole)
        {
            case "host": hosts.Add(person); break;
            case "guest": guests.Add(person); break;
            case "caller": callers.Add(person); break;
            case "mentioned": mentioned.Add(person); break;
        }
        hosts = hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        guests = guests.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        callers = callers.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        mentioned = mentioned.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Execute(connection, transaction, "UPDATE episodes SET hosts=$hosts,callers=$callers,mentioned_people=$mentioned,user_modified=1,updated_at=$now WHERE id=$id",
            ("$hosts", string.Join("|", hosts)), ("$callers", string.Join("|", callers)), ("$mentioned", string.Join("|", mentioned)),
            ("$now", DateTime.UtcNow.ToString("O")), ("$id", episodeId));
        ReplaceNames(connection, transaction, episodeId, "guests", "episode_guests", "guest_id", guests);
        RecordManualFieldProvenance(connection, transaction, episodeId, "hosts", JsonSerializer.Serialize(hosts), "Research quick decision");
        RecordManualFieldProvenance(connection, transaction, episodeId, "guests", JsonSerializer.Serialize(guests), "Research quick decision");
        RecordManualFieldProvenance(connection, transaction, episodeId, "callers", JsonSerializer.Serialize(callers), "Research quick decision");
        RecordManualFieldProvenance(connection, transaction, episodeId, "mentioned_people", JsonSerializer.Serialize(mentioned), "Research quick decision");
        return true;
    }

    private static bool ApplyRemoveWeakTopic(SqliteConnection connection, SqliteTransaction transaction, ResearchQualityFinding finding, ResearchRepairSnapshot snapshot)
    {
        var key = NormaliseValue(finding.DirectDecisionSubject);
        var matching = snapshot.ResearchTopics.Where(x => NormaliseValue(x.Topic) == key).ToArray();
        if (matching.Length == 0) return false;
        var changed = false;
        foreach (var row in matching)
            changed |= Execute(connection, transaction, "DELETE FROM research_topics WHERE id=$id", ("$id", row.Id)) > 0;
        changed |= Execute(connection, transaction, "UPDATE research_broadcasts SET user_modified=1,updated_at=$now WHERE id=$id",
            ("$now", DateTime.UtcNow.ToString("O")), ("$id", finding.ResearchBroadcastId)) > 0;
        if (!snapshot.EpisodeId.HasValue) return changed;
        var tags = snapshot.EpisodeTags.Where(x => NormaliseValue(x) != key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        ReplaceNames(connection, transaction, snapshot.EpisodeId.Value, "tags", "episode_tags", "tag_id", tags);
        Execute(connection, transaction, "UPDATE episodes SET user_modified=1,updated_at=$now WHERE id=$id",
            ("$now", DateTime.UtcNow.ToString("O")), ("$id", snapshot.EpisodeId.Value));
        RecordManualFieldProvenance(connection, transaction, snapshot.EpisodeId.Value, "topics", JsonSerializer.Serialize(tags), "Research quick decision");
        return true;
    }

    private static string ResearchRoleDisplay(string role)
        => role switch
        {
            "host" => "a host",
            "guest" => "a guest",
            "caller" => "a caller",
            "mentioned" => "mentioned only",
            _ => role
        };

    private static bool NormaliseEpisodePipeField(SqliteConnection connection, SqliteTransaction transaction, long episodeId, string column, string value, string key, string canonical)
    {
        var items = SplitPipeValues(value).ToList();
        var any = items.Any(x => NormalisePersonKey(x) == key && !string.Equals(x, canonical, StringComparison.Ordinal));
        if (!any) return false;
        var output = items.Select(x => NormalisePersonKey(x) == key ? canonical : x)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE episodes SET {column}=$value,updated_at=$now WHERE id=$id";
        command.Parameters.AddWithValue("$value", string.Join(" | ", output));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$id", episodeId);
        return command.ExecuteNonQuery() > 0;
    }

    private static long InsertResearchQualityAction(SqliteConnection connection, SqliteTransaction transaction, ResearchQualityFinding finding, ResearchRepairSnapshot before, ResearchRepairSnapshot after)
        => InsertResearchQualityAction(connection, transaction, finding, before, after, finding.SafeFixKind, finding.SafeFixValue);

    private static long InsertResearchQualityAction(SqliteConnection connection, SqliteTransaction transaction, ResearchQualityFinding finding, ResearchRepairSnapshot before, ResearchRepairSnapshot after, string fixKind, string fixValue)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO research_quality_actions(research_broadcast_id,episode_id,rule_id,fix_kind,fix_value,before_json,after_json,created_at)
            VALUES($research,$episode,$rule,$kind,$value,$before,$after,$created);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$research", finding.ResearchBroadcastId);
        command.Parameters.AddWithValue("$episode", finding.EpisodeId.HasValue ? (object)finding.EpisodeId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$rule", finding.RuleId);
        command.Parameters.AddWithValue("$kind", fixKind);
        command.Parameters.AddWithValue("$value", fixValue);
        command.Parameters.AddWithValue("$before", JsonSerializer.Serialize(before, ResearchRepairJsonOptions));
        command.Parameters.AddWithValue("$after", JsonSerializer.Serialize(after, ResearchRepairJsonOptions));
        command.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void TouchResearchRecord(SqliteConnection connection, SqliteTransaction transaction, long researchBroadcastId)
    {
        Execute(connection, transaction, "UPDATE research_broadcasts SET updated_at=$now WHERE id=$id", ("$now", DateTime.UtcNow.ToString("O")), ("$id", researchBroadcastId));
    }

    private static ResearchRepairSnapshot? ReadResearchRepairSnapshot(SqliteConnection connection, long researchBroadcastId, long? episodeId, SqliteTransaction? transaction = null)
    {
        using var header = connection.CreateCommand();
        header.Transaction = transaction;
        header.CommandText = "SELECT headline,episode_id,COALESCE(user_modified,0) FROM research_broadcasts WHERE id=$id";
        header.Parameters.AddWithValue("$id", researchBroadcastId);
        using var reader = header.ExecuteReader();
        if (!reader.Read()) return null;
        var snapshot = new ResearchRepairSnapshot
        {
            ResearchBroadcastId = researchBroadcastId,
            EpisodeId = episodeId ?? (reader.IsDBNull(1) ? null : reader.GetInt64(1)),
            ResearchHeadline = reader.GetString(0),
            ResearchUserModified = reader.GetInt32(2) == 1
        };
        reader.Close();
        snapshot.ResearchSources = ReadResearchSources(connection, transaction, researchBroadcastId);
        snapshot.ResearchPeople = ReadResearchPeople(connection, transaction, researchBroadcastId);
        snapshot.ResearchTopics = ReadResearchTopics(connection, transaction, researchBroadcastId);
        snapshot.ResearchMoments = ReadResearchMoments(connection, transaction, researchBroadcastId);
        if (snapshot.EpisodeId.HasValue)
            ReadEpisodeRepairSnapshot(connection, transaction, snapshot);
        return snapshot;
    }

    private static List<RepairSourceRow> ReadResearchSources(SqliteConnection connection, SqliteTransaction? transaction, long id)
    {
        var rows = new List<RepairSourceRow>();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id,url,title,publisher,source_type,accessed_at,confidence,supports,notes,created_at FROM research_sources WHERE research_broadcast_id=$id ORDER BY id";
        command.Parameters.AddWithValue("$id", id); using var r = command.ExecuteReader();
        while (r.Read()) rows.Add(new RepairSourceRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6), r.GetString(7), r.GetString(8), r.GetString(9)));
        return rows;
    }

    private static List<RepairPersonRow> ReadResearchPeople(SqliteConnection connection, SqliteTransaction? transaction, long id)
    {
        var rows = new List<RepairPersonRow>(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id,name,role,confidence,source_id,notes,created_at FROM research_people WHERE research_broadcast_id=$id ORDER BY id";
        command.Parameters.AddWithValue("$id", id); using var r = command.ExecuteReader();
        while (r.Read()) rows.Add(new RepairPersonRow(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.IsDBNull(4) ? null : r.GetInt64(4), r.GetString(5), r.GetString(6)));
        return rows;
    }

    private static List<RepairTopicRow> ReadResearchTopics(SqliteConnection connection, SqliteTransaction? transaction, long id)
    {
        var rows = new List<RepairTopicRow>(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id,topic,confidence,source_id,notes,created_at FROM research_topics WHERE research_broadcast_id=$id ORDER BY id";
        command.Parameters.AddWithValue("$id", id); using var r = command.ExecuteReader();
        while (r.Read()) rows.Add(new RepairTopicRow(r.GetInt64(0), r.GetString(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt64(3), r.GetString(4), r.GetString(5)));
        return rows;
    }

    private static List<RepairMomentRow> ReadResearchMoments(SqliteConnection connection, SqliteTransaction? transaction, long id)
    {
        var rows = new List<RepairMomentRow>(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id,timestamp_seconds,title,description,tags,confidence,source_id,created_at FROM research_moments WHERE research_broadcast_id=$id ORDER BY id";
        command.Parameters.AddWithValue("$id", id); using var r = command.ExecuteReader();
        while (r.Read()) rows.Add(new RepairMomentRow(r.GetInt64(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetInt32(5), r.IsDBNull(6) ? null : r.GetInt64(6), r.GetString(7)));
        return rows;
    }

    private static void ReadEpisodeRepairSnapshot(SqliteConnection connection, SqliteTransaction? transaction, ResearchRepairSnapshot snapshot)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(title,''),COALESCE(user_modified,0),COALESCE(hosts,''),COALESCE(callers,''),COALESCE(mentioned_people,'') FROM episodes WHERE id=$id";
        command.Parameters.AddWithValue("$id", snapshot.EpisodeId!.Value); using var r = command.ExecuteReader();
        if (r.Read())
        {
            snapshot.EpisodeTitle = r.GetString(0); snapshot.EpisodeUserModified = r.GetInt32(1) == 1;
            snapshot.EpisodeHosts = r.GetString(2); snapshot.EpisodeCallers = r.GetString(3); snapshot.EpisodeMentionedPeople = r.GetString(4);
        }
        r.Close();
        snapshot.EpisodeGuests = ReadEpisodeNames(connection, transaction, snapshot.EpisodeId.Value, "episode_guests", "guests", "guest_id");
        snapshot.EpisodeTags = ReadEpisodeNames(connection, transaction, snapshot.EpisodeId.Value, "episode_tags", "tags", "tag_id");
    }

    private static List<string> ReadEpisodeNames(SqliteConnection connection, SqliteTransaction? transaction, long episodeId, string joinTable, string entityTable, string idColumn)
    {
        var values = new List<string>(); using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = $"SELECT e.name FROM {joinTable} j JOIN {entityTable} e ON e.id=j.{idColumn} WHERE j.episode_id=$id ORDER BY e.name";
        command.Parameters.AddWithValue("$id", episodeId); using var r = command.ExecuteReader(); while (r.Read()) values.Add(r.GetString(0)); return values;
    }

    private static void RestoreResearchRepairSnapshot(SqliteConnection connection, SqliteTransaction transaction, ResearchRepairSnapshot snapshot)
    {
        Execute(connection, transaction, "UPDATE research_broadcasts SET headline=$headline,user_modified=$modified,updated_at=$now WHERE id=$id", ("$headline", snapshot.ResearchHeadline), ("$modified", snapshot.ResearchUserModified ? 1 : 0), ("$now", DateTime.UtcNow.ToString("O")), ("$id", snapshot.ResearchBroadcastId));
        Execute(connection, transaction, "DELETE FROM research_people WHERE research_broadcast_id=$id", ("$id", snapshot.ResearchBroadcastId));
        Execute(connection, transaction, "DELETE FROM research_topics WHERE research_broadcast_id=$id", ("$id", snapshot.ResearchBroadcastId));
        Execute(connection, transaction, "DELETE FROM research_moments WHERE research_broadcast_id=$id", ("$id", snapshot.ResearchBroadcastId));
        Execute(connection, transaction, "DELETE FROM research_sources WHERE research_broadcast_id=$id", ("$id", snapshot.ResearchBroadcastId));
        foreach (var row in snapshot.ResearchSources)
            Execute(connection, transaction, "INSERT INTO research_sources(id,research_broadcast_id,url,title,publisher,source_type,accessed_at,confidence,supports,notes,created_at) VALUES($row,$research,$url,$title,$publisher,$type,$accessed,$confidence,$supports,$notes,$created)",
                ("$row", row.Id), ("$research", snapshot.ResearchBroadcastId), ("$url", row.Url), ("$title", row.Title), ("$publisher", row.Publisher), ("$type", row.SourceType), ("$accessed", row.AccessedAt is null ? (object)DBNull.Value : row.AccessedAt), ("$confidence", row.Confidence), ("$supports", row.Supports), ("$notes", row.Notes), ("$created", row.CreatedAt));
        foreach (var row in snapshot.ResearchPeople)
            Execute(connection, transaction, "INSERT INTO research_people(id,research_broadcast_id,name,role,confidence,source_id,notes,created_at) VALUES($row,$research,$name,$role,$confidence,$source,$notes,$created)",
                ("$row", row.Id), ("$research", snapshot.ResearchBroadcastId), ("$name", row.Name), ("$role", row.Role), ("$confidence", row.Confidence), ("$source", row.SourceId.HasValue ? (object)row.SourceId.Value : DBNull.Value), ("$notes", row.Notes), ("$created", row.CreatedAt));
        foreach (var row in snapshot.ResearchTopics)
            Execute(connection, transaction, "INSERT INTO research_topics(id,research_broadcast_id,topic,confidence,source_id,notes,created_at) VALUES($row,$research,$topic,$confidence,$source,$notes,$created)",
                ("$row", row.Id), ("$research", snapshot.ResearchBroadcastId), ("$topic", row.Topic), ("$confidence", row.Confidence), ("$source", row.SourceId.HasValue ? (object)row.SourceId.Value : DBNull.Value), ("$notes", row.Notes), ("$created", row.CreatedAt));
        foreach (var row in snapshot.ResearchMoments)
            Execute(connection, transaction, "INSERT INTO research_moments(id,research_broadcast_id,timestamp_seconds,title,description,tags,confidence,source_id,created_at) VALUES($row,$research,$seconds,$title,$description,$tags,$confidence,$source,$created)",
                ("$row", row.Id), ("$research", snapshot.ResearchBroadcastId), ("$seconds", row.TimestampSeconds), ("$title", row.Title), ("$description", row.Description), ("$tags", row.Tags), ("$confidence", row.Confidence), ("$source", row.SourceId.HasValue ? (object)row.SourceId.Value : DBNull.Value), ("$created", row.CreatedAt));

        if (!snapshot.EpisodeId.HasValue) return;
        Execute(connection, transaction, "UPDATE episodes SET title=$title,hosts=$hosts,callers=$callers,mentioned_people=$mentioned,user_modified=$modified,updated_at=$now WHERE id=$id",
            ("$title", snapshot.EpisodeTitle), ("$hosts", snapshot.EpisodeHosts), ("$callers", snapshot.EpisodeCallers), ("$mentioned", snapshot.EpisodeMentionedPeople), ("$modified", snapshot.EpisodeUserModified ? 1 : 0), ("$now", DateTime.UtcNow.ToString("O")), ("$id", snapshot.EpisodeId.Value));
        RestoreEpisodeNames(connection, transaction, snapshot.EpisodeId.Value, snapshot.EpisodeGuests, "episode_guests", "guests", "guest_id");
        RestoreEpisodeNames(connection, transaction, snapshot.EpisodeId.Value, snapshot.EpisodeTags, "episode_tags", "tags", "tag_id");
    }

    private static void RestoreEpisodeNames(SqliteConnection connection, SqliteTransaction transaction, long episodeId, IEnumerable<string> names, string joinTable, string entityTable, string idColumn)
    {
        Execute(connection, transaction, $"DELETE FROM {joinTable} WHERE episode_id=$episode", ("$episode", episodeId));
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Execute(connection, transaction, $"INSERT OR IGNORE INTO {entityTable}(name) VALUES($name)", ("$name", name));
            using var id = connection.CreateCommand(); id.Transaction = transaction;
            id.CommandText = $"SELECT id FROM {entityTable} WHERE name=$name COLLATE NOCASE ORDER BY id LIMIT 1"; id.Parameters.AddWithValue("$name", name);
            var entityId = Convert.ToInt64(id.ExecuteScalar());
            Execute(connection, transaction, $"INSERT OR IGNORE INTO {joinTable}(episode_id,{idColumn}) VALUES($episode,$entity)", ("$episode", episodeId), ("$entity", entityId));
        }
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command.ExecuteNonQuery();
    }

    private static bool SameText(string left, string right) => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool SameSourceUrl(string left, string right) => string.Equals(left?.Trim(), right?.Trim(), StringComparison.Ordinal);
    private static bool SameNormalised(string left, string right) => NormaliseValue(left) == NormaliseValue(right);
    private static string NormaliseValue(string value) => string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static string NormalisePersonKey(string value) => new string(NormaliseValue(value).Replace("&", "and", StringComparison.Ordinal).Where(char.IsLetterOrDigit).ToArray());
    private static IEnumerable<string> SplitPipeValues(string value) => (value ?? string.Empty).Split(new[] { '|', ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(x => !string.IsNullOrWhiteSpace(x));

    private sealed class ResearchRepairSnapshot
    {
        public ResearchRepairSnapshot() { }
        public long ResearchBroadcastId { get; set; }
        public long? EpisodeId { get; set; }
        public string ResearchHeadline { get; set; } = string.Empty;
        public bool ResearchUserModified { get; set; }
        public List<RepairSourceRow> ResearchSources { get; set; } = new();
        public List<RepairPersonRow> ResearchPeople { get; set; } = new();
        public List<RepairTopicRow> ResearchTopics { get; set; } = new();
        public List<RepairMomentRow> ResearchMoments { get; set; } = new();
        public string EpisodeTitle { get; set; } = string.Empty;
        public bool EpisodeUserModified { get; set; }
        public string EpisodeHosts { get; set; } = string.Empty;
        public string EpisodeCallers { get; set; } = string.Empty;
        public string EpisodeMentionedPeople { get; set; } = string.Empty;
        public List<string> EpisodeGuests { get; set; } = new();
        public List<string> EpisodeTags { get; set; } = new();
    }

    private sealed class RepairSourceRow
    {
        public RepairSourceRow() { }
        public RepairSourceRow(long id, string url, string title, string publisher, string sourceType, string? accessedAt, int confidence, string supports, string notes, string createdAt)
        {
            Id = id; Url = url; Title = title; Publisher = publisher; SourceType = sourceType; AccessedAt = accessedAt;
            Confidence = confidence; Supports = supports; Notes = notes; CreatedAt = createdAt;
        }
        public long Id { get; set; }
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Publisher { get; set; } = string.Empty;
        public string SourceType { get; set; } = string.Empty;
        public string? AccessedAt { get; set; }
        public int Confidence { get; set; }
        public string Supports { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
    }

    private sealed class RepairPersonRow
    {
        public RepairPersonRow() { }
        public RepairPersonRow(long id, string name, string role, int confidence, long? sourceId, string notes, string createdAt)
        { Id = id; Name = name; Role = role; Confidence = confidence; SourceId = sourceId; Notes = notes; CreatedAt = createdAt; }
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public int Confidence { get; set; }
        public long? SourceId { get; set; }
        public string Notes { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
    }

    private sealed class RepairTopicRow
    {
        public RepairTopicRow() { }
        public RepairTopicRow(long id, string topic, int confidence, long? sourceId, string notes, string createdAt)
        { Id = id; Topic = topic; Confidence = confidence; SourceId = sourceId; Notes = notes; CreatedAt = createdAt; }
        public long Id { get; set; }
        public string Topic { get; set; } = string.Empty;
        public int Confidence { get; set; }
        public long? SourceId { get; set; }
        public string Notes { get; set; } = string.Empty;
        public string CreatedAt { get; set; } = string.Empty;
    }

    private sealed class RepairMomentRow
    {
        public RepairMomentRow() { }
        public RepairMomentRow(long id, int timestampSeconds, string title, string description, string tags, int confidence, long? sourceId, string createdAt)
        { Id = id; TimestampSeconds = timestampSeconds; Title = title; Description = description; Tags = tags; Confidence = confidence; SourceId = sourceId; CreatedAt = createdAt; }
        public long Id { get; set; }
        public int TimestampSeconds { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Tags { get; set; } = string.Empty;
        public int Confidence { get; set; }
        public long? SourceId { get; set; }
        public string CreatedAt { get; set; } = string.Empty;
    }
}
