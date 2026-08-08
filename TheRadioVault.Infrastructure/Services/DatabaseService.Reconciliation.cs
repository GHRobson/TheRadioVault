using Microsoft.Data.Sqlite;
using System.Text.Json;
using TheRadioVault.Core.Services;
using TheRadioVault.Models;

namespace TheRadioVault.Services;

public sealed partial class DatabaseService
{
    private sealed class ReconciliationChangeSet
    {
        public string BeforeHeadline { get; set; } = "";
        public string AfterHeadline { get; set; } = "";
        public string BeforeSummary { get; set; } = "";
        public string AfterSummary { get; set; } = "";
        public string BeforeStation { get; set; } = "";
        public string AfterStation { get; set; } = "";
        public string BeforeSlot { get; set; } = "";
        public string AfterSlot { get; set; } = "";
        public string BeforeVariant { get; set; } = "";
        public string AfterVariant { get; set; } = "";
        public string BeforeEra { get; set; } = "";
        public string AfterEra { get; set; } = "";
        public string BeforeEpisodeType { get; set; } = "";
        public string AfterEpisodeType { get; set; } = "";
        public string BeforeArchiveNotes { get; set; } = "";
        public string AfterArchiveNotes { get; set; } = "";
        public List<string> AddedHosts { get; set; } = new();
        public List<string> AddedGuests { get; set; } = new();
        public List<string> AddedCallers { get; set; } = new();
        public List<string> AddedMentionedPeople { get; set; } = new();
        public List<string> AddedTopics { get; set; } = new();
        public List<long> InsertedMomentIds { get; set; } = new();
        public long? PreviousResearchEpisodeId { get; set; }
        public string PreviousExistenceStatus { get; set; } = "unknown_gap";
        public string PreviousResearchState { get; set; } = "partially_researched";
        public bool PreviousNeedsReview { get; set; }
        public long? AlternateCloneResearchId { get; set; }
    }

    private static string NormalizeDecisionSource(string? source)
        => string.Equals(source, "automatic", StringComparison.OrdinalIgnoreCase) ? "automatic" : "manual";

    public IReadOnlyList<ResearchReconciliationCandidateRecord> GetResearchReconciliationCandidates(
        string status = "pending",
        int limit = 1000,
        long? candidateId = null)
    {
        var result = new List<ResearchReconciliationCandidateRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rrc.id,rrc.research_broadcast_id,rrc.episode_id,rb.episode_id,
                   rrc.score,rrc.reason,rrc.status,rrc.created_at,rrc.updated_at,
                   c.name,rb.air_date,rb.slot,rb.part_number,rb.headline,rb.summary,
                   rb.station,rb.existence_status,rb.confidence,
                   COALESCE(e.title,''),COALESCE(e.description,''),COALESCE(e.edition,''),
                   COALESCE((SELECT mf.original_filename FROM media_files mf
                             WHERE mf.episode_id=e.id AND mf.is_missing=0
                             ORDER BY mf.is_preferred DESC,mf.id LIMIT 1),''),
                   COALESCE(e.user_modified,0),
                   (SELECT COUNT(*) FROM research_people rp WHERE rp.research_broadcast_id=rb.id),
                   (SELECT COUNT(*) FROM research_topics rt WHERE rt.research_broadcast_id=rb.id),
                   (SELECT COUNT(*) FROM research_moments rm WHERE rm.research_broadcast_id=rb.id),
                   (SELECT COUNT(*) FROM research_sources rs WHERE rs.research_broadcast_id=rb.id),
                   EXISTS(SELECT 1 FROM research_reconciliation_actions rra
                          WHERE rra.candidate_id=rrc.id AND rra.action='approved' AND rra.undone_at IS NULL),
                   CASE WHEN rb.episode_id IS NULL THEN 0 ELSE EXISTS(
                       SELECT 1 FROM media_files linked_mf
                       WHERE linked_mf.episode_id=rb.episode_id AND linked_mf.is_missing=0
                   ) END,
                   COALESCE(rrc.requires_review,1),COALESCE(rrc.review_category,'ambiguous_match'),
                   COALESCE(rrc.recommended_action,''),COALESCE(rrc.decision_source,'manual'),
                   COALESCE(rb.edition,''),COALESCE(rb.broadcast_variant,''),
                   COALESCE(rb.broadcast_era,''),COALESCE(rb.episode_type,''),
                   COALESCE(rb.archive_notes,''),COALESCE(e.broadcast_slot,''),
                   COALESCE(e.part_number,1),e.total_parts,
                   COALESCE((SELECT MAX(mf.duration_ms) FROM media_files mf
                             WHERE mf.episode_id=e.id AND mf.is_missing=0),0),
                   COALESCE(rb.source_broadcast_id,''),COALESCE(e.broadcast_uid,''),e.air_date
            FROM research_reconciliation_candidates rrc
            JOIN research_broadcasts rb ON rb.id=rrc.research_broadcast_id
            JOIN episodes e ON e.id=rrc.episode_id
            JOIN collections c ON c.id=rb.collection_id
            WHERE ($status='' OR ($status='not_pending' AND rrc.status<>'pending') OR rrc.status=$status)
              AND ($candidate=0 OR rrc.id=$candidate)
            ORDER BY CASE WHEN rb.episode_id IS NULL THEN 0 ELSE 1 END,
                     rrc.score DESC,rrc.updated_at DESC,rrc.id DESC
            LIMIT $limit
            """;
        command.Parameters.AddWithValue("$status", status?.Trim().ToLowerInvariant() ?? "pending");
        command.Parameters.AddWithValue("$candidate", candidateId ?? 0);
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 20000));
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ResearchReconciliationCandidateRecord
            {
                Id = reader.GetInt64(0),
                ResearchBroadcastId = reader.GetInt64(1),
                EpisodeId = reader.GetInt64(2),
                ExistingResearchEpisodeId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                Score = reader.GetInt32(4),
                Reason = reader.GetString(5),
                Status = reader.GetString(6),
                CreatedAt = ParseResearchTimestamp(reader.GetString(7)),
                UpdatedAt = ParseResearchTimestamp(reader.GetString(8)),
                Show = reader.GetString(9),
                BroadcastDate = ParseResearchDate(reader, 10),
                Slot = reader.GetString(11),
                PartNumber = reader.GetInt32(12),
                ResearchHeadline = reader.GetString(13),
                ResearchSummary = reader.GetString(14),
                ResearchStation = reader.GetString(15),
                ExistenceStatus = reader.GetString(16),
                ResearchConfidence = reader.GetInt32(17),
                EpisodeHeadline = reader.GetString(18),
                EpisodeSummary = reader.GetString(19),
                EpisodeStation = reader.GetString(20),
                OriginalFilename = reader.GetString(21),
                EpisodeUserModified = reader.GetInt32(22) == 1,
                PeopleCount = Convert.ToInt32(reader.GetInt64(23)),
                TopicCount = Convert.ToInt32(reader.GetInt64(24)),
                MomentCount = Convert.ToInt32(reader.GetInt64(25)),
                SourceCount = Convert.ToInt32(reader.GetInt64(26)),
                CanUndo = reader.GetInt32(27) == 1,
                ExistingEpisodeAvailable = reader.GetInt32(28) == 1,
                RequiresReview = reader.GetInt32(29) == 1,
                ReviewCategory = reader.GetString(30),
                RecommendedAction = reader.GetString(31),
                DecisionSource = reader.GetString(32),
                ResearchEdition = reader.GetString(33),
                ResearchVariant = reader.GetString(34),
                ResearchEra = reader.GetString(35),
                ResearchEpisodeType = reader.GetString(36),
                ResearchArchiveNotes = reader.GetString(37),
                EpisodeSlot = reader.GetString(38),
                EpisodePartNumber = reader.GetInt32(39),
                EpisodeTotalParts = reader.IsDBNull(40) ? null : reader.GetInt32(40),
                EpisodeDurationMs = reader.GetInt64(41),
                ResearchSourceBroadcastId = reader.GetString(42),
                EpisodeBroadcastUid = reader.GetString(43),
                EpisodeBroadcastDate = ParseResearchDate(reader, 44)
            });
        }
        return result;
    }

    public int GetPendingResearchReconciliationCount()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(DISTINCT research_broadcast_id) FROM research_reconciliation_candidates WHERE status='pending' AND requires_review=1";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public int GetResearchAttentionCount()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(DISTINCT research_broadcast_id)
                 FROM research_reconciliation_candidates
                 WHERE status='pending' AND requires_review=1)
              + (SELECT COUNT(DISTINCT rb.id) FROM research_broadcasts rb
                 WHERE EXISTS(
                     SELECT 1 FROM research_conflicts rc
                     WHERE rc.research_broadcast_id=rb.id AND rc.resolution='unresolved'
                 )
                   AND NOT EXISTS(
                       SELECT 1 FROM research_reconciliation_candidates rrc
                       WHERE rrc.research_broadcast_id=rb.id
                         AND rrc.status='pending' AND rrc.requires_review=1))
            """;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    public IReadOnlyList<ResearchReconciliationGroupRecord> GetResearchReconciliationGroups(string view = "pending")
    {
        var normalizedView = view?.Trim().ToLowerInvariant() ?? "pending";
        // The decision view is by far the most common path. Loading completed
        // history as well made large restored libraries perform thousands of
        // unnecessary correlated lookups before the window could appear.
        var all = normalizedView == "pending"
            ? GetResearchReconciliationCandidates("pending", 20000)
            : GetResearchReconciliationCandidates("not_pending", 20000);
        IEnumerable<ResearchReconciliationCandidateRecord> filtered = normalizedView switch
        {
            "automatic" => all.Where(x => x.DecisionSource == "automatic" && x.Status != "pending"),
            "history" => all.Where(x => x.Status != "pending"),
            "approved" => all.Where(x => x.Status == "approved"),
            "rejected" => all.Where(x => x.Status == "rejected"),
            _ => all.Where(x => x.Status == "pending" && x.RequiresReview)
        };

        return filtered
            .GroupBy(x => x.ResearchBroadcastId)
            .Select(group => new ResearchReconciliationGroupRecord
            {
                ResearchBroadcastId = group.Key,
                Candidates = group
                    .OrderByDescending(x => x.Status == "approved")
                    .ThenByDescending(x => x.Score)
                    .ThenByDescending(x => x.UpdatedAt)
                    .ToArray()
            })
            .OrderByDescending(x => x.RequiresReview)
            .ThenByDescending(x => x.BestScore)
            .ThenByDescending(x => x.Primary.UpdatedAt)
            .ToArray();
    }

    public ResearchReconciliationOverview GetResearchReconciliationOverview()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              COUNT(DISTINCT CASE WHEN status='pending' AND requires_review=1 THEN research_broadcast_id END),
              COUNT(DISTINCT CASE WHEN status<>'pending' AND decision_source='automatic' THEN research_broadcast_id END),
              COUNT(DISTINCT CASE WHEN status='approved' AND decision_source<>'automatic' THEN research_broadcast_id END),
              COUNT(DISTINCT CASE WHEN status='rejected' THEN research_broadcast_id END),
              SUM(CASE WHEN status='pending' AND requires_review=1 THEN 1 ELSE 0 END),
              COUNT(DISTINCT CASE WHEN status<>'pending' THEN research_broadcast_id END),
              MAX(CASE WHEN status<>'pending' AND decision_source='automatic' THEN updated_at END)
            FROM research_reconciliation_candidates
            """;
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return new ResearchReconciliationOverview();
        return new ResearchReconciliationOverview
        {
            NeedsDecision = reader.IsDBNull(0) ? 0 : Convert.ToInt32(reader.GetInt64(0)),
            AutomaticDecisions = reader.IsDBNull(1) ? 0 : Convert.ToInt32(reader.GetInt64(1)),
            ManualApprovals = reader.IsDBNull(2) ? 0 : Convert.ToInt32(reader.GetInt64(2)),
            Dismissed = reader.IsDBNull(3) ? 0 : Convert.ToInt32(reader.GetInt64(3)),
            PendingCandidateRows = reader.IsDBNull(4) ? 0 : Convert.ToInt32(reader.GetInt64(4)),
            CompletedDecisions = reader.IsDBNull(5) ? 0 : Convert.ToInt32(reader.GetInt64(5)),
            LatestAutomaticDecisionAt = reader.IsDBNull(6) ? null : ParseResearchTimestamp(reader.GetString(6))
        };
    }

    public ResearchReconciliationTriageResult TriageResearchReconciliationCandidates()
    {
        // Scan completion, the Research workspace and the decision window used
        // to be able to start the same expensive pass concurrently. Serialising
        // it prevents SQLite lock contention and duplicate work.
        lock (_researchTriageGate)
            return TriageResearchReconciliationCandidatesCore();
    }

    private ResearchReconciliationTriageResult TriageResearchReconciliationCandidatesCore()
    {
        var result = new ResearchReconciliationTriageResult();

        // Clear the two largest classes of non-decisions in set-based SQL before
        // materialising candidate objects. Full-library portable exports contain
        // thousands of identity/source-only rows, and restored research that is
        // already attached never needs a filename choice.
        result.AutomaticallyDismissed += BulkDismissNonActionablePendingCandidates();

        var pending = GetResearchReconciliationCandidates("pending", 20000);
        RefreshCandidateEvidence(pending);
        var groups = pending.GroupBy(x => x.ResearchBroadcastId).ToArray();

        // Portable full-library packs contain one record per broadcast even when
        // the record carries only identity, source and structured slot/station
        // fields. Link these in one transaction rather than performing thousands
        // of full metadata applications and candidate-detail reloads.
        var identityGroups = groups
            .Where(group => !group.Any(x => x.ReviewCategory == "manual_hold")
                            && group.All(IsIdentityOrSourceOnly))
            .ToArray();
        if (identityGroups.Length > 0)
        {
            var identityResult = BulkLinkIdentityOrSourceOnlyGroups(identityGroups);
            result.AutomaticallyApplied += identityResult.LinkedGroups;
            result.AutomaticallyDismissed += identityResult.DismissedCandidates;
            var handledIds = identityGroups.Select(x => x.Key).ToHashSet();
            groups = groups.Where(x => !handledIds.Contains(x.Key)).ToArray();
        }

        foreach (var group in groups)
        {
            var candidates = group.OrderByDescending(x => x.Score).ThenBy(x => x.Id).ToArray();
            if (candidates.Length == 0) continue;
            if (candidates.Any(x => x.ReviewCategory == "manual_hold"))
            {
                UpdateCandidateTriage(candidates.Select(x => x.Id), true, "choose_broadcast",
                    "This automatic decision was undone. Choose the correct broadcast or leave the research unlinked.", "manual");
                result.GroupsNeedingDecision++;
                result.CandidateRowsNeedingDecision += candidates.Length;
                continue;
            }

            // A restored research record that is already attached to an available
            // broadcast needs no approval merely because other same-day recordings
            // exist.
            if (candidates[0].ExistingResearchEpisodeId.HasValue && candidates[0].ExistingEpisodeAvailable)
            {
                DismissCandidatesAutomatically(
                    candidates.Select(x => x.Id),
                    "research_already_attached",
                    "No action is required. The saved research is already linked to an available broadcast.");
                result.AutomaticallyDismissed += candidates.Length;
                continue;
            }

            candidates = PruneStructurallyIncompatibleCandidates(candidates, result).ToArray();
            if (candidates.Length == 0)
            {
                ClearResearchReviewIfNoPending(group.Key);
                continue;
            }

            // Full-library exports contain an identity row for every broadcast,
            // even when no headline, summary, people, topics, Moments, source or
            // other research metadata exists. Choosing between recordings cannot
            // improve an empty record, so link it to a sensible local representative
            // without interrupting the user.
            if (candidates.All(IsIdentityOrSourceOnly))
            {
                // This is normally removed by the set-based pre-pass. Keep a
                // defensive path for records that changed while triage was
                // running, but do not perform a full metadata application for an
                // empty archive identity.
                DismissCandidatesAutomatically(candidates.Select(x => x.Id), "identity_only",
                    "No action is required. This identity/source-only record contains no research payload.");
                result.AutomaticallyDismissed += candidates.Length;
                ClearResearchReviewIfNoPending(group.Key);
                continue;
            }

            var exact = candidates.Where(IsDeterministicIdentityMatch).ToArray();
            if ((!candidates[0].ExistingResearchEpisodeId.HasValue || !candidates[0].ExistingEpisodeAvailable)
                && exact.Length == 1)
            {
                if (TryApplyAutomatically(
                    exact[0],
                    candidates,
                    result,
                    category: "exact_identity",
                    recommendation: "Radio Vault attached this exact broadcast identity automatically.",
                    copyMoments: true,
                    applyResearchPayload: true))
                    continue;

                result.GroupsNeedingDecision++;
                result.CandidateRowsNeedingDecision += candidates.Length;
                continue;
            }

            // Multiple files or multipart segments from the same show, date and
            // normalised slot belong to one logical broadcast family. Prefer an
            // exact part when one exists, but do not ask the user to reconcile
            // ordinary parts or encodes merely because an older export assigned a
            // different synthetic part number. Timed Moments remain reviewable
            // because a different capture can have a shifted timeline.
            if (IsSingleLogicalBroadcastFamily(candidates))
            {
                if (!candidates[0].HasTimedMoments
                    && TryApplyAutomatically(
                        ChooseCanonicalCandidate(candidates),
                        candidates,
                        result,
                        category: "same_broadcast_family",
                        recommendation: "Recording variants were grouped as one logical broadcast and reconciled automatically.",
                        copyMoments: false,
                        applyResearchPayload: true))
                    continue;

                UpdateCandidateTriage(candidates.Select(x => x.Id), true, "multipart_timeline",
                    "Choose the recording whose timeline matches the saved Moments. Broadcast metadata itself is shared across this multipart/alternate-capture family.", "manual");
                result.GroupsNeedingDecision++;
                result.CandidateRowsNeedingDecision += candidates.Length;
                continue;
            }

            var category = ClassifyReviewGroup(candidates, exact);
            var recommendation = category switch
            {
                "slot_ambiguity" => "Choose the regular, Midday, OpieRadio, AM/PM, or other same-day slot described by the research.",
                "alternate_capture" => "Keep the existing link unless the new recording is a genuinely separate capture that should share this research.",
                "choose_broadcast" => "Select the one broadcast whose current slot and part best fit the research. Nothing is changed until you apply it.",
                _ => "Compare the filename and saved research, then choose the best match or leave the research unlinked."
            };
            UpdateCandidateTriage(candidates.Select(x => x.Id), true, category, recommendation, "manual");
            result.GroupsNeedingDecision++;
            result.CandidateRowsNeedingDecision += candidates.Length;
        }

        return result;
    }

    private static bool IsIdentityOrSourceOnly(ResearchReconciliationCandidateRecord candidate)
        => string.IsNullOrWhiteSpace(candidate.ResearchHeadline)
           && string.IsNullOrWhiteSpace(candidate.ResearchSummary)
           && string.IsNullOrWhiteSpace(candidate.ResearchArchiveNotes)
           && candidate.PeopleCount == 0
           && candidate.TopicCount == 0
           && candidate.MomentCount == 0;

    private (int LinkedGroups, int DismissedCandidates) BulkLinkIdentityOrSourceOnlyGroups(
        IReadOnlyList<IGrouping<long, ResearchReconciliationCandidateRecord>> groups)
    {
        if (groups.Count == 0) return (0, 0);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        var linked = 0;
        var dismissed = 0;

        foreach (var group in groups)
        {
            var candidates = group.OrderByDescending(x => x.Score).ThenBy(x => x.Id).ToArray();
            if (candidates.Length == 0) continue;
            var winner = ChooseCanonicalCandidate(candidates);

            using (var research = connection.CreateCommand())
            {
                research.Transaction = transaction;
                research.CommandText = """
                    UPDATE research_broadcasts
                       SET episode_id=$episode,existence_status='in_library',
                           research_state=CASE WHEN research_state IN('fully_researched','conflicting_information','alternate_capture','encore_or_replay','special_edition') THEN research_state ELSE 'in_library' END,needs_review=CASE WHEN EXISTS(
                               SELECT 1 FROM research_conflicts rc
                                WHERE rc.research_broadcast_id=research_broadcasts.id
                                  AND rc.resolution='unresolved'
                           ) THEN 1 ELSE 0 END,
                           attached_at=COALESCE(attached_at,$now),updated_at=$now
                     WHERE id=$research
                    """;
                research.Parameters.AddWithValue("$episode", winner.EpisodeId);
                research.Parameters.AddWithValue("$now", now);
                research.Parameters.AddWithValue("$research", group.Key);
                research.ExecuteNonQuery();
            }

            // Structured identity fields are safe to fill when the local episode
            // has no manually protected value. Editorial research is deliberately
            // excluded from this fast path.
            using (var episode = connection.CreateCommand())
            {
                episode.Transaction = transaction;
                episode.CommandText = """
                    UPDATE episodes SET
                        edition=CASE WHEN user_modified=0 AND TRIM(COALESCE(edition,''))=''
                                     AND TRIM($station)<>'' THEN $station ELSE edition END,
                        broadcast_slot=CASE WHEN user_modified=0 AND TRIM(COALESCE(broadcast_slot,''))=''
                                            AND TRIM($slot)<>'' THEN $slot ELSE broadcast_slot END,
                        broadcast_variant=CASE WHEN user_modified=0 AND TRIM(COALESCE(broadcast_variant,''))=''
                                               AND TRIM($variant)<>'' THEN $variant ELSE broadcast_variant END,
                        broadcast_era=CASE WHEN user_modified=0 AND TRIM(COALESCE(broadcast_era,''))=''
                                           AND TRIM($era)<>'' THEN $era ELSE broadcast_era END,
                        episode_type=CASE WHEN user_modified=0 AND TRIM(COALESCE(episode_type,''))=''
                                          AND TRIM($type)<>'' THEN $type ELSE episode_type END,
                        updated_at=$now
                    WHERE id=$episode
                    """;
                episode.Parameters.AddWithValue("$station", winner.ResearchStation ?? string.Empty);
                episode.Parameters.AddWithValue("$slot", winner.Slot ?? string.Empty);
                episode.Parameters.AddWithValue("$variant", winner.ResearchVariant ?? string.Empty);
                episode.Parameters.AddWithValue("$era", winner.ResearchEra ?? string.Empty);
                episode.Parameters.AddWithValue("$type", winner.ResearchEpisodeType ?? string.Empty);
                episode.Parameters.AddWithValue("$now", now);
                episode.Parameters.AddWithValue("$episode", winner.EpisodeId);
                episode.ExecuteNonQuery();
            }

            using (var approve = connection.CreateCommand())
            {
                approve.Transaction = transaction;
                approve.CommandText = """
                    UPDATE research_reconciliation_candidates
                       SET status=CASE WHEN id=$winner THEN 'approved' ELSE 'rejected' END,
                           requires_review=0,review_category='identity_only',
                           recommended_action='Identity/source-only archive record linked automatically; no editorial research required a decision.',
                           decision_source='automatic',updated_at=$now
                     WHERE research_broadcast_id=$research AND status='pending'
                    """;
                approve.Parameters.AddWithValue("$winner", winner.Id);
                approve.Parameters.AddWithValue("$now", now);
                approve.Parameters.AddWithValue("$research", group.Key);
                approve.ExecuteNonQuery();
            }

            linked++;
            dismissed += Math.Max(0, candidates.Length - 1);
        }

        transaction.Commit();
        return (linked, dismissed);
    }

    private int BulkDismissNonActionablePendingCandidates()
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTime.UtcNow.ToString("O");
        var dismissed = 0;

        using (var attached = connection.CreateCommand())
        {
            attached.Transaction = transaction;
            attached.CommandText = """
                UPDATE research_reconciliation_candidates
                   SET status='rejected',requires_review=0,
                       review_category='research_already_attached',
                       recommended_action='No action is required. The saved research is already linked to an available broadcast.',
                       decision_source='automatic',updated_at=$now
                 WHERE status='pending'
                   AND COALESCE(review_category,'')<>'manual_hold'
                   AND EXISTS(
                       SELECT 1
                         FROM research_broadcasts rb
                        WHERE rb.id=research_reconciliation_candidates.research_broadcast_id
                          AND rb.episode_id IS NOT NULL
                          AND EXISTS(
                              SELECT 1 FROM media_files mf
                               WHERE mf.episode_id=rb.episode_id AND mf.is_missing=0
                          )
                   )
                """;
            attached.Parameters.AddWithValue("$now", now);
            dismissed += attached.ExecuteNonQuery();
        }

        // Keep the research record's badge in sync without opening one
        // connection and transaction per researched broadcast.
        using (var refreshReview = connection.CreateCommand())
        {
            refreshReview.Transaction = transaction;
            refreshReview.CommandText = """
                UPDATE research_broadcasts
                   SET needs_review=CASE
                       WHEN EXISTS(SELECT 1 FROM research_conflicts rc
                                   WHERE rc.research_broadcast_id=research_broadcasts.id
                                     AND rc.resolution='unresolved') THEN 1
                       WHEN EXISTS(SELECT 1 FROM research_reconciliation_candidates rrc
                                   WHERE rrc.research_broadcast_id=research_broadcasts.id
                                     AND rrc.status='pending' AND rrc.requires_review=1) THEN 1
                       ELSE 0 END,
                       updated_at=$now
                 WHERE id IN(
                       SELECT DISTINCT research_broadcast_id
                         FROM research_reconciliation_candidates
                        WHERE updated_at=$now AND status='rejected'
                          AND decision_source='automatic'
                          AND review_category='research_already_attached'
                 )
                """;
            refreshReview.Parameters.AddWithValue("$now", now);
            refreshReview.ExecuteNonQuery();
        }

        transaction.Commit();
        return dismissed;
    }

    private void RefreshCandidateEvidence(IReadOnlyList<ResearchReconciliationCandidateRecord> candidates)
    {
        if (candidates.Count == 0) return;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var candidate in candidates)
        {
            var score = 0;
            var reasons = new List<string>();
            if (candidate.BroadcastDate.HasValue && candidate.EpisodeBroadcastDate.HasValue
                && candidate.BroadcastDate.Value.Date == candidate.EpisodeBroadcastDate.Value.Date)
            {
                score += 60;
                reasons.Add("same show and exact broadcast date");
            }
            else if (!candidate.BroadcastDate.HasValue || !candidate.EpisodeBroadcastDate.HasValue)
            {
                score += 10;
                reasons.Add("date incomplete");
            }
            else
            {
                reasons.Add("broadcast date differs");
            }

            if (candidate.PartNumber == Math.Max(1, candidate.EpisodePartNumber))
            {
                score += 15;
                reasons.Add("same part number");
            }
            else if (candidate.PartNumber > 1 || candidate.EpisodePartNumber > 1)
            {
                score -= 20;
                reasons.Add("part number differs");
            }

            if (BroadcastSlotNormalizer.Equivalent(candidate.Slot, candidate.EpisodeSlot))
            {
                score += 15;
                reasons.Add("same broadcast slot");
            }
            else if (!string.IsNullOrWhiteSpace(candidate.Slot)
                     && !string.IsNullOrWhiteSpace(candidate.EpisodeSlot))
            {
                score -= 10;
                reasons.Add("broadcast slot differs");
            }

            if (!string.IsNullOrWhiteSpace(candidate.ResearchSourceBroadcastId)
                && !string.IsNullOrWhiteSpace(candidate.EpisodeBroadcastUid)
                && string.Equals(candidate.ResearchSourceBroadcastId.Trim(), candidate.EpisodeBroadcastUid.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                score += 60;
                reasons.Add("same stable broadcast identifier");
            }
            else if (!string.IsNullOrWhiteSpace(candidate.ResearchSourceBroadcastId)
                     && Path.GetFileNameWithoutExtension(candidate.OriginalFilename)
                         .Contains(candidate.ResearchSourceBroadcastId, StringComparison.OrdinalIgnoreCase))
            {
                score += 15;
                reasons.Add("broadcast identifier appears in filename");
            }

            score = Math.Clamp(score, 0, 100);
            var reason = string.Join("; ", reasons);
            var changed = candidate.Score != score
                          || !string.Equals(candidate.Reason, reason, StringComparison.Ordinal);
            candidate.Score = score;
            candidate.Reason = reason;
            if (!changed) continue;

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE research_reconciliation_candidates SET score=$score,reason=$reason,updated_at=$now WHERE id=$id AND status='pending'";
            update.Parameters.AddWithValue("$score", score);
            update.Parameters.AddWithValue("$reason", reason);
            update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$id", candidate.Id);
            update.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private IReadOnlyList<ResearchReconciliationCandidateRecord> PruneStructurallyIncompatibleCandidates(
        IReadOnlyList<ResearchReconciliationCandidateRecord> candidates,
        ResearchReconciliationTriageResult result)
    {
        var remaining = candidates.ToList();
        if (remaining.Count == 0) return remaining;

        var exactPart = remaining.Where(x => x.EpisodePartNumber == Math.Max(1, x.PartNumber)).ToArray();
        if (exactPart.Length > 0 && exactPart.Length < remaining.Count)
        {
            var rejected = remaining.Except(exactPart).ToArray();
            DismissCandidatesAutomatically(rejected.Select(x => x.Id), "different_multipart_segment",
                "No action is required. This file is a different multipart segment from the saved research identity.");
            result.AutomaticallyDismissed += rejected.Length;
            remaining = exactPart.ToList();
        }

        var researchSlot = remaining[0].Slot;
        if (!string.IsNullOrWhiteSpace(researchSlot))
        {
            var explicitEquivalent = remaining
                .Where(x => !string.IsNullOrWhiteSpace(x.EpisodeSlot)
                            && BroadcastSlotNormalizer.Equivalent(researchSlot, x.EpisodeSlot))
                .ToArray();
            if (explicitEquivalent.Length > 0 && explicitEquivalent.Length < remaining.Count)
            {
                var rejected = remaining.Except(explicitEquivalent).ToArray();
                DismissCandidatesAutomatically(rejected.Select(x => x.Id), "different_broadcast_slot",
                    "No action is required. A more specific candidate matches the saved AM/PM, Midday, OpieRadio or other broadcast slot.");
                result.AutomaticallyDismissed += rejected.Length;
                remaining = explicitEquivalent.ToList();
            }
            else if (explicitEquivalent.Length == 0)
            {
                var conflicting = remaining
                    .Where(x => !string.IsNullOrWhiteSpace(x.EpisodeSlot)
                                && !BroadcastSlotNormalizer.Equivalent(researchSlot, x.EpisodeSlot))
                    .ToArray();
                if (conflicting.Length > 0 && conflicting.Length < remaining.Count)
                {
                    DismissCandidatesAutomatically(conflicting.Select(x => x.Id), "different_broadcast_slot",
                        "No action is required. This candidate belongs to a different same-day broadcast slot.");
                    result.AutomaticallyDismissed += conflicting.Length;
                    remaining = remaining.Except(conflicting).ToList();
                }
            }
        }

        return remaining;
    }

    private bool TryApplyAutomatically(
        ResearchReconciliationCandidateRecord winner,
        IReadOnlyList<ResearchReconciliationCandidateRecord> candidates,
        ResearchReconciliationTriageResult result,
        string category,
        string recommendation,
        bool copyMoments,
        bool applyResearchPayload)
    {
        UpdateCandidateTriage(new[] { winner.Id }, false, category, recommendation, "automatic");
        try
        {
            var details = GetResearchReconciliationCandidateDetails(winner.Id)
                ?? throw new InvalidOperationException("The reconciliation candidate could not be read.");
            ApplyResearchReconciliationCandidate(winner.Id, new ResearchReconciliationApplyOptions
            {
                ApplyHeadline = applyResearchPayload
                    && !details.Episode.UserModified
                    && string.IsNullOrWhiteSpace(details.Episode.Headline)
                    && !string.IsNullOrWhiteSpace(details.Research.Record.Headline),
                ApplySummary = applyResearchPayload
                    && !details.Episode.UserModified
                    && string.IsNullOrWhiteSpace(details.Episode.Description)
                    && !string.IsNullOrWhiteSpace(details.Research.Record.Summary),
                ApplyBroadcastDetails = applyResearchPayload
                    && !details.Episode.UserModified
                    && string.IsNullOrWhiteSpace(details.Episode.Edition),
                MergePeople = applyResearchPayload,
                MergeTopics = applyResearchPayload,
                CopyMoments = applyResearchPayload && copyMoments,
                DecisionSource = "automatic"
            });
            result.AutomaticallyApplied++;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Research reconciliation", $"Automatic reconciliation failed for candidate {winner.Id}.", ex);
            UpdateCandidateTriage(new[] { winner.Id }, true, "automatic_apply_failed",
                "Automatic application failed. Review this match and choose Apply selected.", "manual");
            return false;
        }

        var losingIds = candidates.Where(x => x.Id != winner.Id).Select(x => x.Id).ToArray();
        if (losingIds.Length > 0)
        {
            DismissCandidatesAutomatically(losingIds, category,
                "No action is required. These files are other parts or recording variants of the same logical broadcast.");
            result.AutomaticallyDismissed += losingIds.Length;
        }
        return true;
    }

    private static ResearchReconciliationCandidateRecord ChooseCanonicalCandidate(
        IReadOnlyList<ResearchReconciliationCandidateRecord> candidates)
    {
        return candidates
            .OrderByDescending(x => x.Reason.Contains("same stable broadcast identifier", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.EpisodePartNumber == Math.Max(1, x.PartNumber))
            .ThenByDescending(x => BroadcastSlotNormalizer.Equivalent(x.Slot, x.EpisodeSlot))
            .ThenBy(x => MultipartParser.Detect(x.OriginalFilename).IsMultipart ? 1 : 0)
            .ThenByDescending(x => x.EpisodeDurationMs)
            .ThenByDescending(x => x.Score)
            .ThenBy(x => x.Id)
            .First();
    }

    private static bool IsSingleLogicalBroadcastFamily(IReadOnlyList<ResearchReconciliationCandidateRecord> candidates)
    {
        if (candidates.Count == 0) return false;
        var first = candidates[0];
        if (!first.BroadcastDate.HasValue || !first.EpisodeBroadcastDate.HasValue) return false;

        var date = first.EpisodeBroadcastDate.Value.Date;
        var slot = BroadcastSlotNormalizer.Canonicalize(first.EpisodeSlot);
        return candidates.All(x =>
            x.BroadcastDate.HasValue
            && x.EpisodeBroadcastDate.HasValue
            && x.BroadcastDate.Value.Date == x.EpisodeBroadcastDate.Value.Date
            && x.EpisodeBroadcastDate.Value.Date == date
            && BroadcastSlotNormalizer.Canonicalize(x.EpisodeSlot) == slot
            && (string.IsNullOrWhiteSpace(x.Slot)
                || string.IsNullOrWhiteSpace(x.EpisodeSlot)
                || BroadcastSlotNormalizer.Equivalent(x.Slot, x.EpisodeSlot)));
    }

    private void ClearResearchReviewIfNoPending(long researchBroadcastId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE research_broadcasts SET
                needs_review=CASE WHEN EXISTS(
                    SELECT 1 FROM research_conflicts rc
                    WHERE rc.research_broadcast_id=$research AND rc.resolution='unresolved'
                ) THEN 1 ELSE 0 END,
                updated_at=$now
            WHERE id=$research
              AND NOT EXISTS(
                  SELECT 1 FROM research_reconciliation_candidates rrc
                  WHERE rrc.research_broadcast_id=$research
                    AND rrc.status='pending' AND rrc.requires_review=1
              )
            """;
        command.Parameters.AddWithValue("$research", researchBroadcastId);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static bool IsDeterministicIdentityMatch(ResearchReconciliationCandidateRecord candidate)
    {
        if (candidate.Score < ResearchReconciliationRules.StrongMatchThreshold) return false;
        var reason = candidate.Reason ?? string.Empty;
        return reason.Contains("same stable broadcast identifier", StringComparison.OrdinalIgnoreCase)
               || (reason.Contains("exact broadcast date", StringComparison.OrdinalIgnoreCase)
                   && reason.Contains("same part number", StringComparison.OrdinalIgnoreCase)
                   && reason.Contains("same broadcast slot", StringComparison.OrdinalIgnoreCase));
    }

    private static string ClassifyReviewGroup(
        IReadOnlyList<ResearchReconciliationCandidateRecord> candidates,
        IReadOnlyList<ResearchReconciliationCandidateRecord> exact)
    {
        if (candidates.Any(x => x.IsAlternateCapture)) return "alternate_capture";
        if (candidates.Count > 1)
        {
            var slotFamilies = candidates
                .Select(x => BroadcastSlotNormalizer.Canonicalize(x.EpisodeSlot))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (slotFamilies.Length > 1) return "slot_ambiguity";

            var filenames = string.Join(" ", candidates.Select(x => x.OriginalFilename));
            if (filenames.Contains("midday", StringComparison.OrdinalIgnoreCase)
                || filenames.Contains("opie", StringComparison.OrdinalIgnoreCase)
                || filenames.Contains(" OR ", StringComparison.OrdinalIgnoreCase))
                return "slot_ambiguity";
            return "choose_broadcast";
        }
        return exact.Count == 0 ? "low_confidence" : "choose_broadcast";
    }

    private void UpdateCandidateTriage(
        IEnumerable<long> candidateIds,
        bool requiresReview,
        string category,
        string recommendation,
        string decisionSource)
    {
        var ids = candidateIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var id in ids)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE research_reconciliation_candidates
                SET requires_review=$review,review_category=$category,
                    recommended_action=$recommendation,decision_source=$source,updated_at=$now
                WHERE id=$id AND status='pending'
                """;
            command.Parameters.AddWithValue("$review", requiresReview ? 1 : 0);
            command.Parameters.AddWithValue("$category", category);
            command.Parameters.AddWithValue("$recommendation", recommendation);
            command.Parameters.AddWithValue("$source", NormalizeDecisionSource(decisionSource));
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private void DismissCandidatesAutomatically(
        IEnumerable<long> candidateIds,
        string category,
        string recommendation)
    {
        var ids = candidateIds.Distinct().ToArray();
        if (ids.Length == 0) return;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        foreach (var id in ids)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE research_reconciliation_candidates
                SET status='rejected',requires_review=0,review_category=$category,
                    recommended_action=$recommendation,decision_source='automatic',updated_at=$now
                WHERE id=$id AND status='pending'
                """;
            command.Parameters.AddWithValue("$category", category);
            command.Parameters.AddWithValue("$recommendation", recommendation);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public ResearchReconciliationCandidateDetails? GetResearchReconciliationCandidateDetails(long candidateId)
    {
        var candidate = GetResearchReconciliationCandidates("", 1, candidateId).FirstOrDefault();
        if (candidate is null) return null;
        var research = GetResearchLibraryRecordDetails(candidate.ResearchBroadcastId);
        if (research is null) return null;
        return new ResearchReconciliationCandidateDetails
        {
            Candidate = candidate,
            Research = research,
            Episode = GetRichEpisodeMetadata(candidate.EpisodeId)
        };
    }

    public ResearchReconciliationApplyResult ApplyResearchReconciliationCandidate(
        long candidateId,
        ResearchReconciliationApplyOptions options)
    {
        options ??= new ResearchReconciliationApplyOptions();
        var details = GetResearchReconciliationCandidateDetails(candidateId)
            ?? throw new InvalidOperationException("The reconciliation candidate could not be read.");
        if (!string.Equals(details.Candidate.Status, "pending", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This reconciliation candidate has already been reviewed.");

        using var connection = OpenConnection();
        var item = TryDeserializeResearchBroadcast(connection, details.Candidate.ResearchBroadcastId)
            ?? throw new InvalidOperationException("The saved research payload could not be read.");
        item.Research ??= new TrvPackResearch();
        item.Research.Broadcast ??= new TrvPackBroadcastMetadata();
        item.Research.People ??= new TrvPackPeople();
        item.Research.Topics ??= new List<string>();
        item.Research.Moments ??= new List<TrvPackMoment>();

        var change = new ReconciliationChangeSet();
        using var transaction = connection.BeginTransaction();

        ReadEpisodeAndResearchState(connection, transaction, details.Candidate, change,
            out var currentHosts, out var currentGuests, out var currentCallers,
            out var currentMentioned, out var currentTopics);

        var research = item.Research;
        var broadcast = research.Broadcast;
        change.AfterHeadline = options.ApplyHeadline && !string.IsNullOrWhiteSpace(research.Headline)
            ? research.Headline.Trim() : change.BeforeHeadline;
        change.AfterSummary = options.ApplySummary && !string.IsNullOrWhiteSpace(research.Summary)
            ? research.Summary.Trim() : change.BeforeSummary;
        change.AfterStation = options.ApplyBroadcastDetails && !string.IsNullOrWhiteSpace(broadcast.Station)
            ? broadcast.Station.Trim() : change.BeforeStation;
        change.AfterSlot = options.ApplyBroadcastDetails && !string.IsNullOrWhiteSpace(broadcast.Slot)
            ? broadcast.Slot.Trim() : change.BeforeSlot;
        change.AfterVariant = options.ApplyBroadcastDetails && !string.IsNullOrWhiteSpace(broadcast.Variant)
            ? broadcast.Variant.Trim() : change.BeforeVariant;
        change.AfterEra = options.ApplyBroadcastDetails && !string.IsNullOrWhiteSpace(broadcast.Era)
            ? broadcast.Era.Trim() : change.BeforeEra;
        change.AfterEpisodeType = options.ApplyBroadcastDetails && !string.IsNullOrWhiteSpace(broadcast.EpisodeType)
            ? broadcast.EpisodeType.Trim() : change.BeforeEpisodeType;
        change.AfterArchiveNotes = options.ApplyBroadcastDetails && !string.IsNullOrWhiteSpace(research.ArchiveNotes)
            ? research.ArchiveNotes.Trim() : change.BeforeArchiveNotes;

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE episodes SET title=$headline,description=$summary,edition=$station,
                    broadcast_slot=$slot,broadcast_variant=$variant,broadcast_era=$era,
                    episode_type=$type,archive_notes=$notes,updated_at=$now
                WHERE id=$episode
                """;
            update.Parameters.AddWithValue("$headline", change.AfterHeadline);
            update.Parameters.AddWithValue("$summary", change.AfterSummary);
            update.Parameters.AddWithValue("$station", change.AfterStation);
            update.Parameters.AddWithValue("$slot", change.AfterSlot);
            update.Parameters.AddWithValue("$variant", change.AfterVariant);
            update.Parameters.AddWithValue("$era", change.AfterEra);
            update.Parameters.AddWithValue("$type", change.AfterEpisodeType);
            update.Parameters.AddWithValue("$notes", change.AfterArchiveNotes);
            update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            update.Parameters.AddWithValue("$episode", details.Candidate.EpisodeId);
            update.ExecuteNonQuery();
        }

        if (options.MergePeople)
        {
            var mergedHosts = MergeResearchNames(currentHosts, research.People.Hosts, change.AddedHosts);
            var mergedGuests = MergeResearchNames(currentGuests, research.People.Guests.Concat(research.Guests ?? new List<string>()), change.AddedGuests);
            var mergedCallers = MergeResearchNames(currentCallers, research.People.Callers, change.AddedCallers);
            var mergedMentioned = MergeResearchNames(currentMentioned, research.People.MentionedPeople, change.AddedMentionedPeople);
            UpdatePipePeople(connection, transaction, details.Candidate.EpisodeId, mergedHosts, mergedCallers, mergedMentioned);
            ReplaceNames(connection, transaction, details.Candidate.EpisodeId, "guests", "episode_guests", "guest_id", mergedGuests);
        }

        if (options.MergeTopics)
        {
            var mergedTopics = MergeResearchNames(currentTopics, research.Topics, change.AddedTopics);
            ReplaceNames(connection, transaction, details.Candidate.EpisodeId, "tags", "episode_tags", "tag_id", mergedTopics);
        }

        if (options.CopyMoments)
            CopyResearchMoments(connection, transaction, details.Candidate.EpisodeId, research.Moments, change.InsertedMomentIds);

        long attachedResearchId;
        var alternate = details.Candidate.IsAlternateCapture || options.CreateAlternateCapture;
        if (alternate)
        {
            attachedResearchId = CloneResearchForAlternateCapture(
                connection, transaction, details.Candidate.ResearchBroadcastId, details.Candidate.EpisodeId);
            change.AlternateCloneResearchId = attachedResearchId;
        }
        else
        {
            attachedResearchId = details.Candidate.ResearchBroadcastId;
            using var attach = connection.CreateCommand();
            attach.Transaction = transaction;
            attach.CommandText = """
                UPDATE research_broadcasts SET episode_id=$episode,existence_status='in_library',
                    research_state=CASE WHEN EXISTS(
                        SELECT 1 FROM research_conflicts rc
                        WHERE rc.research_broadcast_id=$research AND rc.resolution='unresolved'
                    ) THEN 'conflicting_information' ELSE 'in_library' END,
                    needs_review=CASE WHEN EXISTS(
                        SELECT 1 FROM research_conflicts rc
                        WHERE rc.research_broadcast_id=$research AND rc.resolution='unresolved'
                    ) THEN 1 ELSE 0 END,
                    attached_at=$now,updated_at=$now WHERE id=$research
                """;
            attach.Parameters.AddWithValue("$episode", details.Candidate.EpisodeId);
            attach.Parameters.AddWithValue("$research", attachedResearchId);
            attach.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            attach.ExecuteNonQuery();
        }

        using (var approve = connection.CreateCommand())
        {
            approve.Transaction = transaction;
            approve.CommandText = "UPDATE research_reconciliation_candidates SET status='approved',requires_review=0,decision_source=$source,updated_at=$now WHERE id=$id";
            approve.Parameters.AddWithValue("$id", candidateId);
            approve.Parameters.AddWithValue("$source", NormalizeDecisionSource(options.DecisionSource));
            approve.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            approve.ExecuteNonQuery();
        }

        using (var action = connection.CreateCommand())
        {
            action.Transaction = transaction;
            action.CommandText = """
                INSERT INTO research_reconciliation_actions(
                    candidate_id,research_broadcast_id,episode_id,action,options_json,change_json,decision_source,created_at)
                VALUES($candidate,$research,$episode,'approved',$options,$changes,$source,$now)
                """;
            action.Parameters.AddWithValue("$candidate", candidateId);
            action.Parameters.AddWithValue("$research", details.Candidate.ResearchBroadcastId);
            action.Parameters.AddWithValue("$episode", details.Candidate.EpisodeId);
            action.Parameters.AddWithValue("$options", JsonSerializer.Serialize(options));
            action.Parameters.AddWithValue("$changes", JsonSerializer.Serialize(change));
            action.Parameters.AddWithValue("$source", NormalizeDecisionSource(options.DecisionSource));
            action.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            action.ExecuteNonQuery();
        }

        transaction.Commit();
        if (!alternate)
            SyncLegacyResearchState(connection, details.Candidate.ResearchBroadcastId, "resolved", details.Candidate.EpisodeId,
                $"{(NormalizeDecisionSource(options.DecisionSource) == "automatic" ? "Applied automatically" : "Approved from reconciliation review")}. Applied {options.AppliedFieldsDisplay}.");

        return new ResearchReconciliationApplyResult
        {
            Applied = true,
            CreatedAlternateCapture = alternate,
            PeopleAdded = change.AddedHosts.Count + change.AddedGuests.Count + change.AddedCallers.Count + change.AddedMentionedPeople.Count,
            TopicsAdded = change.AddedTopics.Count,
            MomentsAdded = change.InsertedMomentIds.Count,
            Summary = alternate
                ? "Research was copied to a new alternate-capture record and attached to this recording."
                : $"Research was attached. Applied {options.AppliedFieldsDisplay}."
        };
    }

    public void DismissOtherResearchReconciliationCandidates(long researchBroadcastId, long selectedCandidateId)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE research_reconciliation_candidates
            SET status='rejected',requires_review=0,review_category='superseded_candidate',
                recommended_action='No action is required. Another candidate was selected for this research.',
                decision_source='manual',updated_at=$now
            WHERE research_broadcast_id=$research AND id<>$selected AND status='pending'
            """;
        command.Parameters.AddWithValue("$research", researchBroadcastId);
        command.Parameters.AddWithValue("$selected", selectedCandidateId);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    public void RejectResearchReconciliationCandidate(long candidateId)
    {
        var details = GetResearchReconciliationCandidateDetails(candidateId)
            ?? throw new InvalidOperationException("The reconciliation candidate could not be read.");
        if (!string.Equals(details.Candidate.Status, "pending", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This reconciliation candidate has already been reviewed.");
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE research_reconciliation_candidates SET status='rejected',requires_review=0,decision_source='manual',updated_at=$now WHERE id=$id";
            command.Parameters.AddWithValue("$id", candidateId);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        using (var action = connection.CreateCommand())
        {
            action.Transaction = transaction;
            action.CommandText = """
                INSERT INTO research_reconciliation_actions(
                    candidate_id,research_broadcast_id,episode_id,action,created_at)
                VALUES($candidate,$research,$episode,'rejected',$now)
                """;
            action.Parameters.AddWithValue("$candidate", candidateId);
            action.Parameters.AddWithValue("$research", details.Candidate.ResearchBroadcastId);
            action.Parameters.AddWithValue("$episode", details.Candidate.EpisodeId);
            action.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            action.ExecuteNonQuery();
        }
        if (!details.Candidate.ExistingResearchEpisodeId.HasValue)
        {
            using var clearReview = connection.CreateCommand();
            clearReview.Transaction = transaction;
            clearReview.CommandText = """
                UPDATE research_broadcasts SET
                    needs_review=CASE
                        WHEN EXISTS(SELECT 1 FROM research_conflicts rc WHERE rc.research_broadcast_id=$research AND rc.resolution='unresolved') THEN 1
                        WHEN EXISTS(SELECT 1 FROM research_reconciliation_candidates rrc WHERE rrc.research_broadcast_id=$research AND rrc.status='pending') THEN 1
                        ELSE 0 END,
                    updated_at=$now
                WHERE id=$research
                """;
            clearReview.Parameters.AddWithValue("$research", details.Candidate.ResearchBroadcastId);
            clearReview.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            clearReview.ExecuteNonQuery();
        }
        transaction.Commit();
        if (!details.Candidate.ExistingResearchEpisodeId.HasValue)
            SyncLegacyResearchState(connection, details.Candidate.ResearchBroadcastId, "ignored", null,
                "Rejected as a reconciliation match. The saved research remains preserved.");
    }

    public ResearchReconciliationUndoResult UndoResearchReconciliationCandidate(long candidateId)
    {
        using var connection = OpenConnection();
        long actionId;
        long researchId;
        long episodeId;
        string changeJson;
        using (var read = connection.CreateCommand())
        {
            read.CommandText = """
                SELECT id,research_broadcast_id,episode_id,change_json
                FROM research_reconciliation_actions
                WHERE candidate_id=$candidate AND action='approved' AND undone_at IS NULL
                ORDER BY id DESC LIMIT 1
                """;
            read.Parameters.AddWithValue("$candidate", candidateId);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
                return new ResearchReconciliationUndoResult { Summary = "There is no active approved reconciliation to undo." };
            actionId = reader.GetInt64(0);
            researchId = reader.GetInt64(1);
            episodeId = reader.GetInt64(2);
            changeJson = reader.GetString(3);
        }

        var change = JsonSerializer.Deserialize<ReconciliationChangeSet>(changeJson)
            ?? throw new InvalidOperationException("The reconciliation undo record is incomplete.");
        var partial = false;
        using var transaction = connection.BeginTransaction();

        RestoreScalarIfUnchanged(connection, transaction, episodeId, "title", change.AfterHeadline, change.BeforeHeadline, ref partial);
        RestoreScalarIfUnchanged(connection, transaction, episodeId, "description", change.AfterSummary, change.BeforeSummary, ref partial);
        RestoreScalarIfUnchanged(connection, transaction, episodeId, "edition", change.AfterStation, change.BeforeStation, ref partial);
        RestoreScalarIfUnchanged(connection, transaction, episodeId, "broadcast_slot", change.AfterSlot, change.BeforeSlot, ref partial);
        RestoreScalarIfUnchanged(connection, transaction, episodeId, "broadcast_variant", change.AfterVariant, change.BeforeVariant, ref partial);
        RestoreScalarIfUnchanged(connection, transaction, episodeId, "broadcast_era", change.AfterEra, change.BeforeEra, ref partial);
        RestoreScalarIfUnchanged(connection, transaction, episodeId, "episode_type", change.AfterEpisodeType, change.BeforeEpisodeType, ref partial);
        RestoreScalarIfUnchanged(connection, transaction, episodeId, "archive_notes", change.AfterArchiveNotes, change.BeforeArchiveNotes, ref partial);

        RemovePipeAdditions(connection, transaction, episodeId, "hosts", change.AddedHosts);
        RemovePipeAdditions(connection, transaction, episodeId, "callers", change.AddedCallers);
        RemovePipeAdditions(connection, transaction, episodeId, "mentioned_people", change.AddedMentionedPeople);
        RemoveRelationalAdditions(connection, transaction, episodeId, "guests", "episode_guests", "guest_id", change.AddedGuests);
        RemoveRelationalAdditions(connection, transaction, episodeId, "tags", "episode_tags", "tag_id", change.AddedTopics);
        foreach (var momentId in change.InsertedMomentIds)
        {
            using var deleteMoment = connection.CreateCommand();
            deleteMoment.Transaction = transaction;
            deleteMoment.CommandText = "DELETE FROM moments WHERE id=$id AND episode_id=$episode";
            deleteMoment.Parameters.AddWithValue("$id", momentId);
            deleteMoment.Parameters.AddWithValue("$episode", episodeId);
            deleteMoment.ExecuteNonQuery();
        }

        if (change.AlternateCloneResearchId.HasValue)
        {
            using var deleteClone = connection.CreateCommand();
            deleteClone.Transaction = transaction;
            deleteClone.CommandText = "DELETE FROM research_broadcasts WHERE id=$id AND episode_id=$episode";
            deleteClone.Parameters.AddWithValue("$id", change.AlternateCloneResearchId.Value);
            deleteClone.Parameters.AddWithValue("$episode", episodeId);
            deleteClone.ExecuteNonQuery();
        }
        else
        {
            using var restoreResearch = connection.CreateCommand();
            restoreResearch.Transaction = transaction;
            restoreResearch.CommandText = """
                UPDATE research_broadcasts SET episode_id=$episode,existence_status=$existence,
                    research_state=$state,needs_review=$review,attached_at=NULL,updated_at=$now
                WHERE id=$research
                """;
            restoreResearch.Parameters.AddWithValue("$episode", change.PreviousResearchEpisodeId.HasValue ? change.PreviousResearchEpisodeId.Value : DBNull.Value);
            restoreResearch.Parameters.AddWithValue("$existence", change.PreviousExistenceStatus);
            restoreResearch.Parameters.AddWithValue("$state", change.PreviousResearchState);
            restoreResearch.Parameters.AddWithValue("$review", change.PreviousNeedsReview ? 1 : 0);
            restoreResearch.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            restoreResearch.Parameters.AddWithValue("$research", researchId);
            restoreResearch.ExecuteNonQuery();
        }

        using (var candidate = connection.CreateCommand())
        {
            candidate.Transaction = transaction;
            candidate.CommandText = "UPDATE research_reconciliation_candidates SET status='pending',requires_review=1,review_category='manual_hold',recommended_action='Choose the correct broadcast or leave the research unlinked.',decision_source='manual',updated_at=$now WHERE id=$id";
            candidate.Parameters.AddWithValue("$id", candidateId);
            candidate.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            candidate.ExecuteNonQuery();
        }
        using (var action = connection.CreateCommand())
        {
            action.Transaction = transaction;
            action.CommandText = "UPDATE research_reconciliation_actions SET undone_at=$now WHERE id=$id";
            action.Parameters.AddWithValue("$id", actionId);
            action.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            action.ExecuteNonQuery();
        }
        using (var restoreAlternatives = connection.CreateCommand())
        {
            restoreAlternatives.Transaction = transaction;
            restoreAlternatives.CommandText = """
                UPDATE research_reconciliation_candidates
                SET status='pending',requires_review=1,review_category='manual_hold',
                    recommended_action='Choose the correct broadcast or leave the research unlinked.',
                    decision_source='manual',updated_at=$now
                WHERE research_broadcast_id=$research AND id<>$candidate
                  AND status='rejected'
                  AND review_category='superseded_candidate'
                """;
            restoreAlternatives.Parameters.AddWithValue("$research", researchId);
            restoreAlternatives.Parameters.AddWithValue("$candidate", candidateId);
            restoreAlternatives.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            restoreAlternatives.ExecuteNonQuery();
        }
        transaction.Commit();

        if (!change.AlternateCloneResearchId.HasValue)
            SyncLegacyResearchState(connection, researchId, "ambiguous", null,
                "The approved reconciliation was undone and returned to review.");

        return new ResearchReconciliationUndoResult
        {
            Undone = true,
            Partial = partial,
            Summary = partial
                ? "The reconciliation link and safe additions were undone. Later edits to changed scalar fields were preserved."
                : "The reconciliation was undone and returned to Needs your decision."
        };
    }

    private static void ReadEpisodeAndResearchState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ResearchReconciliationCandidateRecord candidate,
        ReconciliationChangeSet change,
        out List<string> hosts,
        out List<string> guests,
        out List<string> callers,
        out List<string> mentioned,
        out List<string> topics)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT COALESCE(title,''),COALESCE(description,''),COALESCE(edition,''),
                       COALESCE(broadcast_slot,''),COALESCE(broadcast_variant,''),
                       COALESCE(broadcast_era,''),COALESCE(episode_type,''),
                       COALESCE(archive_notes,''),COALESCE(hosts,''),COALESCE(callers,''),
                       COALESCE(mentioned_people,'')
                FROM episodes WHERE id=$episode
                  AND EXISTS(SELECT 1 FROM media_files mf WHERE mf.episode_id=episodes.id AND mf.is_missing=0)
                """;
            command.Parameters.AddWithValue("$episode", candidate.EpisodeId);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("The archive broadcast is no longer available.");
            change.BeforeHeadline = change.AfterHeadline = reader.GetString(0);
            change.BeforeSummary = change.AfterSummary = reader.GetString(1);
            change.BeforeStation = change.AfterStation = reader.GetString(2);
            change.BeforeSlot = change.AfterSlot = reader.GetString(3);
            change.BeforeVariant = change.AfterVariant = reader.GetString(4);
            change.BeforeEra = change.AfterEra = reader.GetString(5);
            change.BeforeEpisodeType = change.AfterEpisodeType = reader.GetString(6);
            change.BeforeArchiveNotes = change.AfterArchiveNotes = reader.GetString(7);
            hosts = SplitPipe(reader.GetString(8)).ToList();
            callers = SplitPipe(reader.GetString(9)).ToList();
            mentioned = SplitPipe(reader.GetString(10)).ToList();
        }
        guests = ReadNamesInTransaction(connection, transaction, candidate.EpisodeId, "episode_guests", "guests", "guest_id");
        topics = ReadNamesInTransaction(connection, transaction, candidate.EpisodeId, "episode_tags", "tags", "tag_id");

        using var research = connection.CreateCommand();
        research.Transaction = transaction;
        research.CommandText = "SELECT episode_id,existence_status,research_state,needs_review FROM research_broadcasts WHERE id=$id";
        research.Parameters.AddWithValue("$id", candidate.ResearchBroadcastId);
        using var researchReader = research.ExecuteReader();
        if (!researchReader.Read()) throw new InvalidOperationException("The saved research record no longer exists.");
        change.PreviousResearchEpisodeId = researchReader.IsDBNull(0) ? null : researchReader.GetInt64(0);
        change.PreviousExistenceStatus = researchReader.GetString(1);
        change.PreviousResearchState = researchReader.GetString(2);
        change.PreviousNeedsReview = researchReader.GetInt32(3) == 1;
    }

    private static List<string> ReadNamesInTransaction(SqliteConnection connection, SqliteTransaction transaction, long episodeId, string joinTable, string entityTable, string idColumn)
    {
        var result = new List<string>();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT e.name FROM {joinTable} j JOIN {entityTable} e ON e.id=j.{idColumn} WHERE j.episode_id=$episode ORDER BY e.name COLLATE NOCASE";
        command.Parameters.AddWithValue("$episode", episodeId);
        using var reader = command.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    private static List<string> MergeResearchNames(IEnumerable<string> existing, IEnumerable<string>? incoming, List<string> added)
    {
        var result = existing.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var value in incoming ?? Array.Empty<string>())
        {
            var name = value?.Trim() ?? "";
            if (name.Length == 0 || result.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            result.Add(name);
            added.Add(name);
        }
        return result;
    }

    private static void UpdatePipePeople(SqliteConnection connection, SqliteTransaction transaction, long episodeId, IEnumerable<string> hosts, IEnumerable<string> callers, IEnumerable<string> mentioned)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE episodes SET hosts=$hosts,callers=$callers,mentioned_people=$mentioned,updated_at=$now WHERE id=$episode";
        command.Parameters.AddWithValue("$hosts", string.Join("|", hosts));
        command.Parameters.AddWithValue("$callers", string.Join("|", callers));
        command.Parameters.AddWithValue("$mentioned", string.Join("|", mentioned));
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$episode", episodeId);
        command.ExecuteNonQuery();
    }

    private static void CopyResearchMoments(SqliteConnection connection, SqliteTransaction transaction, long episodeId, IEnumerable<TrvPackMoment> moments, List<long> insertedIds)
    {
        foreach (var moment in moments ?? Array.Empty<TrvPackMoment>())
        {
            var title = moment.Title?.Trim() ?? "";
            if (title.Length == 0) continue;
            var positionMs = Math.Max(0, moment.TimestampSeconds) * 1000;
            var before = FindEquivalentMomentId(
                connection,
                transaction,
                episodeId,
                positionMs,
                title,
                moment.Description?.Trim() ?? "");
            var id = AddMomentIdempotent(
                connection,
                transaction,
                episodeId,
                positionMs,
                title,
                moment.Description?.Trim() ?? "",
                DateTime.UtcNow.ToString("O"));
            if (!before.HasValue) insertedIds.Add(id);
        }
    }

    private static long CloneResearchForAlternateCapture(SqliteConnection connection, SqliteTransaction transaction, long sourceResearchId, long episodeId)
    {
        long cloneId;
        using (var clone = connection.CreateCommand())
        {
            clone.Transaction = transaction;
            clone.CommandText = """
                INSERT INTO research_broadcasts(
                    identity_key,collection_id,episode_id,source_broadcast_id,air_date,slot,
                    part_number,total_parts,capture_key,headline,summary,station,edition,
                    broadcast_variant,broadcast_era,episode_type,archive_notes,research_json,
                    research_state,existence_status,confidence,confidence_reason,user_modified,
                    needs_review,import_run_id,attached_at,created_at,updated_at)
                SELECT identity_key||':alternate:'||$episode,collection_id,$episode,source_broadcast_id,
                    air_date,slot,part_number,total_parts,'alternate-'||$episode,headline,summary,
                    station,edition,broadcast_variant,broadcast_era,episode_type,archive_notes,
                    research_json,'alternate_capture','in_library',confidence,confidence_reason,
                    user_modified,0,import_run_id,$now,$now,$now
                FROM research_broadcasts WHERE id=$source;
                SELECT last_insert_rowid();
                """;
            clone.Parameters.AddWithValue("$episode", episodeId);
            clone.Parameters.AddWithValue("$source", sourceResearchId);
            clone.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            cloneId = Convert.ToInt64(clone.ExecuteScalar());
        }
        CopyResearchChildren(connection, transaction, "research_sources", cloneId, sourceResearchId,
            "url,title,publisher,source_type,accessed_at,confidence,supports,notes,created_at");
        CopyResearchChildren(connection, transaction, "research_people", cloneId, sourceResearchId,
            "name,role,confidence,source_id,notes,created_at", clearSourceId: true);
        CopyResearchChildren(connection, transaction, "research_topics", cloneId, sourceResearchId,
            "topic,confidence,source_id,notes,created_at", clearSourceId: true);
        CopyResearchChildren(connection, transaction, "research_moments", cloneId, sourceResearchId,
            "timestamp_seconds,title,description,tags,confidence,source_id,created_at", clearSourceId: true);
        CopyResearchChildren(connection, transaction, "research_aliases", cloneId, sourceResearchId,
            "alias_type,alias_value,confidence");
        return cloneId;
    }

    private static void CopyResearchChildren(SqliteConnection connection, SqliteTransaction transaction, string table, long cloneId, long sourceId, string columns, bool clearSourceId = false)
    {
        var selectColumns = clearSourceId ? columns.Replace("source_id", "NULL", StringComparison.Ordinal) : columns;
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT OR IGNORE INTO {table}(research_broadcast_id,{columns}) SELECT $clone,{selectColumns} FROM {table} WHERE research_broadcast_id=$source";
        command.Parameters.AddWithValue("$clone", cloneId);
        command.Parameters.AddWithValue("$source", sourceId);
        command.ExecuteNonQuery();
    }

    private static void RestoreScalarIfUnchanged(SqliteConnection connection, SqliteTransaction transaction, long episodeId, string column, string after, string before, ref bool wasPartial)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"UPDATE episodes SET {column}=$before,updated_at=$now WHERE id=$episode AND COALESCE({column},'')=$after";
        command.Parameters.AddWithValue("$before", before);
        command.Parameters.AddWithValue("$after", after);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$episode", episodeId);
        if (command.ExecuteNonQuery() == 0 && !string.Equals(after, before, StringComparison.Ordinal)) wasPartial = true;
    }

    private static void RemovePipeAdditions(SqliteConnection connection, SqliteTransaction transaction, long episodeId, string column, IEnumerable<string> additions)
    {
        var remove = additions.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (remove.Count == 0) return;
        string current;
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = $"SELECT COALESCE({column},'') FROM episodes WHERE id=$episode";
            read.Parameters.AddWithValue("$episode", episodeId);
            current = Convert.ToString(read.ExecuteScalar()) ?? "";
        }
        var kept = SplitPipe(current).Where(x => !remove.Contains(x)).ToList();
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = $"UPDATE episodes SET {column}=$value,updated_at=$now WHERE id=$episode";
        update.Parameters.AddWithValue("$value", string.Join("|", kept));
        update.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
        update.Parameters.AddWithValue("$episode", episodeId);
        update.ExecuteNonQuery();
    }

    private static void RemoveRelationalAdditions(SqliteConnection connection, SqliteTransaction transaction, long episodeId, string entityTable, string joinTable, string idColumn, IEnumerable<string> additions)
    {
        foreach (var name in additions.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = $"DELETE FROM {joinTable} WHERE episode_id=$episode AND {idColumn}=(SELECT id FROM {entityTable} WHERE lower(name)=lower($name) LIMIT 1)";
            command.Parameters.AddWithValue("$episode", episodeId);
            command.Parameters.AddWithValue("$name", name);
            command.ExecuteNonQuery();
        }
    }
}
