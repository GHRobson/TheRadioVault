using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Alpha9 field-level conflict-policy refinement shared by the disposable
/// rehearsal and alpha10 guarded live adoption. Every live decision must
/// reproduce the persisted rehearsal signature exactly before commit. All
/// competing values remain preserved in the permanent forensic audit.
/// </summary>
public sealed partial class LibraryTruthAdoptionRehearsalService
{
    private static readonly MetadataFieldRule[] MetadataFieldRules =
    new MetadataFieldRule[]
    {
        new("collection_id", "Collection", MetadataFieldKind.Structural),
        new("air_date", "Broadcast date", MetadataFieldKind.Structural),
        new("title", "Headline", MetadataFieldKind.Text),
        new("description", "Summary", MetadataFieldKind.Text),
        new("notes", "Notes", MetadataFieldKind.Text),
        new("artwork_path", "Artwork", MetadataFieldKind.Text),
        new("broadcast_slot", "Broadcast slot", MetadataFieldKind.Structural),
        new("edition", "Station", MetadataFieldKind.Text),
        new("archive_notes", "Archive notes", MetadataFieldKind.Text),
        new("broadcast_variant", "Broadcast variant", MetadataFieldKind.Text),
        new("broadcast_era", "Broadcast era", MetadataFieldKind.Text),
        new("episode_type", "Episode type", MetadataFieldKind.Text),
        new("research_sources", "Research sources", MetadataFieldKind.List),
        new("hosts", "Hosts", MetadataFieldKind.List),
        new("callers", "Callers", MetadataFieldKind.List),
        new("mentioned_people", "Mentioned people", MetadataFieldKind.List)
    };

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex DateOnlyRegex = new(@"^(?:19|20)\d{2}[-_/ .]\d{1,2}[-_/ .]\d{1,2}$", RegexOptions.Compiled);
    private static readonly Regex FilenamePartTitleRegex = new(
        @"\b(?:pt|part)\.?\s*[-_]?\d+\b|\b\d+\s*of\s*\d+\b|[-_]pt\d+|\b128k\w*\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FilenameDateTitleRegex = new(
        @"^(?:(?:mon|tue|wed|thu|fri|sat|sun)[a-z]*[-_ ]*)?(?:(?:jan|feb|mar|apr|may|jun|jul|aug|sep|sept|oct|nov|dec)[a-z]*[-_ ]*\d{1,2}[-_ ]*\d{2,4}|\d{1,2}[-_/]\d{1,2}[-_/]\d{2,4})(?:[-_ ].*)?$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RecordingLevelVariantRegex = new(
        @"^(?:split )?archive recording(?: -)? part \d+(?: of \d+)?$|^archive part \d+$|^primary archive recording$|^(?:alternate )?archive[- ]only capture$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public IReadOnlyList<LibraryTruthConflictForensic> GetLatestConflictForensics(int limit = 50000)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id,c.rehearsal_run_id,c.canonical_key,c.field_name,c.conflict_kind,c.classification,
                   c.selected_episode_id,c.selected_value,c.candidate_values_json,c.provenance_json,c.resolution,
                   c.auto_resolved,c.requires_review,c.confidence_score,c.preserved_alternate_count,c.evidence
              FROM library_truth_rehearsal_conflicts c
             WHERE c.rehearsal_run_id=(SELECT COALESCE(MAX(id),0) FROM library_truth_rehearsal_runs)
             ORDER BY c.requires_review DESC,c.canonical_key,c.field_name,c.id
             LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 50000));
        var result = new List<LibraryTruthConflictForensic>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new LibraryTruthConflictForensic(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetInt64(6), reader.GetString(7),
                FormatForensicCandidates(reader.GetString(8)), FormatForensicProvenance(reader.GetString(9)),
                reader.GetString(10), reader.GetInt64(11) != 0, reader.GetInt64(12) != 0, reader.GetInt32(13),
                reader.GetInt32(14), reader.GetString(15)));
        }
        return result;
    }

    private List<ConflictForensicResult> AnalyzeMetadataForensics(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long truthRunId,
        string canonicalKey,
        IReadOnlyList<long> episodeIds,
        long survivorEpisodeId)
    {
        var snapshots = LoadEpisodeSnapshots(connection, transaction, episodeIds);
        var provenance = LoadProvenance(connection, transaction, episodeIds);
        var canonical = LoadCanonicalIdentity(connection, transaction, truthRunId, canonicalKey);
        var results = new List<ConflictForensicResult>();

        foreach (var rule in MetadataFieldRules)
        {
            var candidates = snapshots
                .Select(snapshot => BuildCandidate(snapshot, rule, provenance, survivorEpisodeId))
                .ToArray();
            var result = ClassifyMetadataField(canonicalKey, rule, candidates, canonical, survivorEpisodeId);
            if (result is null) continue;

            results.Add(result);
            if (result.AutoResolved && !result.RequiresReview)
                ApplySelectedMetadataValue(connection, transaction, survivorEpisodeId, rule.Column, result.SelectedValue);
        }

        return results;
    }

    private HeadlineAnalysisResult AnalyzeHeadlineReviewForensics(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string canonicalKey,
        IReadOnlyList<long> episodeIds,
        long survivorEpisodeId)
    {
        var idSql = string.Join(",", episodeIds);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT episode_id,candidate,reviewed_headline,confidence,reasoning,decision,parser_version,updated_at FROM headline_reviews WHERE episode_id IN ({idSql}) ORDER BY episode_id";
        var rows = new List<HeadlineReviewSnapshot>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(new HeadlineReviewSnapshot(
                    reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetString(6), reader.GetString(7)));
            }
        }

        if (rows.Count == 0) return new(Array.Empty<ConflictForensicResult>(), 0);
        if (rows.Count == 1)
        {
            if (rows[0].EpisodeId == survivorEpisodeId) return new(Array.Empty<ConflictForensicResult>(), 0);
            Execute(connection, transaction,
                "UPDATE headline_reviews SET episode_id=$survivor WHERE episode_id=$episode",
                ("$survivor", survivorEpisodeId), ("$episode", rows[0].EpisodeId));
            return new(Array.Empty<ConflictForensicResult>(), 1);
        }

        var scored = rows.Select(row => new
        {
            Row = row,
            Normalized = string.Join("|", NormalizeText(row.Candidate), NormalizeText(row.ReviewedHeadline), NormalizeText(row.Decision)),
            Score = HeadlineScore(row)
        }).OrderByDescending(x => x.Score).ThenByDescending(x => x.Row.EpisodeId == survivorEpisodeId).ThenBy(x => x.Row.EpisodeId).ToArray();
        var distinct = scored.Select(x => x.Normalized).Distinct(StringComparer.Ordinal).ToArray();
        var top = scored[0];
        var accepted = scored.Where(x => string.Equals(x.Row.Decision, "Accepted", StringComparison.OrdinalIgnoreCase)).ToArray();
        var reviewed = scored.Where(x => !string.IsNullOrWhiteSpace(x.Row.Decision)).ToArray();
        var decisiveAccepted = accepted.Length == 1;
        var decisiveReviewed = !decisiveAccepted && accepted.Length == 0 && reviewed.Length == 1;
        var chosen = decisiveAccepted ? accepted[0] : decisiveReviewed ? reviewed[0] : top;
        var runnerScore = scored.Where(x => !ReferenceEquals(x, chosen)).Select(x => x.Score).DefaultIfEmpty(0).Max();
        var margin = chosen.Score - runnerScore;
        var equivalent = distinct.Length <= 1;
        // Multiple non-equivalent human review decisions are never ranked
        // against each other automatically. Quality ranking is reserved for
        // still-pending generated review candidates.
        var qualityWinner = !equivalent && !decisiveAccepted && !decisiveReviewed && reviewed.Length == 0 && margin >= 20;
        var auto = equivalent || decisiveAccepted || decisiveReviewed || qualityWinner;
        var classification = equivalent ? "Equivalent headline reviews"
            : decisiveAccepted ? "Accepted headline review"
            : decisiveReviewed ? "Reviewed headline decision"
            : qualityWinner ? "Quality-ranked headline review"
            : "Genuine headline-review conflict";
        var resolution = equivalent ? "normalise_equivalent"
            : decisiveAccepted ? "use_accepted_review"
            : decisiveReviewed ? "use_reviewed_decision"
            : qualityWinner ? "use_higher_quality_review"
            : "manual_review";
        var preserved = Math.Max(0, distinct.Length - 1);
        var candidatesJson = JsonSerializer.Serialize(rows.Select(x => new
        {
            episodeId = x.EpisodeId,
            value = x.Candidate,
            confidence = x.Confidence,
            reasoning = x.Reasoning,
            reviewedHeadline = x.ReviewedHeadline,
            decision = x.Decision,
            parserVersion = x.ParserVersion,
            updatedAt = x.UpdatedAt,
            score = HeadlineScore(x)
        }), _json);
        var evidence = auto
            ? $"{rows.Count:N0} headline-review rows were compared. {classification} selected episode {chosen.Row.EpisodeId:N0}; every alternate review remains in this forensic row."
            : $"{rows.Count:N0} materially different headline-review rows have comparable evidence. No candidate was selected automatically.";
        var forensic = new ConflictForensicResult(
            canonicalKey, "headline_review", "Review record", classification, auto ? chosen.Row.EpisodeId : null,
            auto ? HeadlineSelectedValue(chosen.Row) : string.Empty, candidatesJson, candidatesJson, resolution, auto, !auto,
            auto ? decisiveAccepted ? 99 : decisiveReviewed ? 95 : equivalent ? 94 : Math.Clamp(70 + Math.Max(0, margin), 70, 98) : 35, preserved, evidence);

        if (!auto) return new(new[] { forensic }, 0);

        Execute(connection, transaction, $"DELETE FROM headline_reviews WHERE episode_id IN ({idSql})");
        Execute(connection, transaction, """
            INSERT INTO headline_reviews(episode_id,candidate,reviewed_headline,confidence,reasoning,decision,parser_version,updated_at)
            VALUES($episode,$candidate,$reviewed,$confidence,$reasoning,$decision,$parser,$updated)
            """,
            ("$episode", survivorEpisodeId), ("$candidate", chosen.Row.Candidate), ("$reviewed", chosen.Row.ReviewedHeadline),
            ("$confidence", chosen.Row.Confidence), ("$reasoning", chosen.Row.Reasoning), ("$decision", chosen.Row.Decision),
            ("$parser", chosen.Row.ParserVersion), ("$updated", chosen.Row.UpdatedAt));
        return new(new[] { forensic }, rows.Count);
    }

    private AliasAnalysisResult AnalyzeRemainingAliasReferences(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string canonicalKey,
        IReadOnlyList<long> aliasEpisodeIds,
        long survivorEpisodeId)
    {
        if (aliasEpisodeIds.Count == 0) return new(Array.Empty<ConflictForensicResult>(), 0);
        var aliasSql = string.Join(",", aliasEpisodeIds);
        var results = new List<ConflictForensicResult>();
        var stateRows = 0;

        var reconciliationRows = ScalarInt(connection, transaction,
            $"SELECT COUNT(*) FROM research_reconciliation_candidates WHERE episode_id IN ({aliasSql})");
        if (reconciliationRows > 0)
        {
            var candidateJson = ReadRowsAsJson(connection, transaction,
                $"SELECT id,research_broadcast_id,episode_id,score,status,review_category,recommended_action,decision_source FROM research_reconciliation_candidates WHERE episode_id IN ({aliasSql}) ORDER BY id");
            stateRows += Execute(connection, transaction,
                $"DELETE FROM research_reconciliation_candidates WHERE episode_id IN ({aliasSql}) AND EXISTS(SELECT 1 FROM research_reconciliation_candidates s WHERE s.episode_id=$survivor AND s.research_broadcast_id=research_reconciliation_candidates.research_broadcast_id)",
                ("$survivor", survivorEpisodeId));
            results.Add(new ConflictForensicResult(
                canonicalKey, "research_reconciliation_candidates", "Alias reference", "Duplicate reference",
                survivorEpisodeId, $"Episode {survivorEpisodeId}", candidateJson, candidateJson, "deduplicate_reference",
                true, false, 100, reconciliationRows, $"{reconciliationRows:N0} alias reconciliation reference(s) duplicated a survivor link and were safely deduplicated inside the disposable transaction."));
        }

        foreach (var reference in new[]
        {
            new AliasReferenceRule("transcript_imports", "Transcript import references"),
            new AliasReferenceRule("voice_samples", "Voice sample references")
        })
        {
            var count = ScalarInt(connection, transaction, $"SELECT COUNT(*) FROM {reference.Table} WHERE episode_id IN ({aliasSql})");
            if (count == 0) continue;
            var json = ReadRowsAsJson(connection, transaction, $"SELECT * FROM {reference.Table} WHERE episode_id IN ({aliasSql}) ORDER BY id");
            results.Add(new ConflictForensicResult(
                canonicalKey, reference.Table, "Alias reference", reference.DisplayName, null, string.Empty,
                json, json, "manual_review", false, true, 25, count,
                $"{count:N0} {reference.DisplayName.ToLowerInvariant()} remain attached to mapped aliases. They are preserved and require an explicit transcript/voice migration policy."));
        }

        return new(results, stateRows);
    }

    private ConflictForensicResult? ClassifyMetadataField(
        string canonicalKey,
        MetadataFieldRule rule,
        IReadOnlyList<MetadataCandidate> candidates,
        CanonicalIdentity canonical,
        long survivorEpisodeId)
    {
        var nonEmpty = candidates.Where(x => x.Normalized.Length > 0).ToArray();
        if (nonEmpty.Length == 0) return null;
        var hasEmpty = candidates.Any(x => x.Normalized.Length == 0);
        var authoritativeEmpty = candidates.Any(x => x.Normalized.Length == 0 && HasFieldLevelAuthority(x));
        var normalizedGroups = nonEmpty.GroupBy(x => x.Normalized, StringComparer.Ordinal).ToArray();
        var rawDistinct = nonEmpty.Select(x => x.Value.Trim()).Distinct(StringComparer.Ordinal).Count();
        if (!hasEmpty && normalizedGroups.Length == 1 && rawDistinct == 1) return null;

        MetadataCandidate? selectedCandidate = null;
        // Keep conservative review defaults so field-policy branches that
        // deliberately fall through to the shared ranking policy satisfy C#
        // definite-assignment analysis without changing runtime behaviour.
        string selectedValue = string.Empty;
        string classification = "Genuine conflicting values";
        string resolution = "manual_review";
        bool autoResolved = false;
        bool requiresReview = true;
        int confidence = 20;

        if (rule.Kind == MetadataFieldKind.Structural)
        {
            selectedValue = canonical.ValueFor(rule.Column);
            selectedCandidate = candidates.FirstOrDefault(x => string.Equals(x.Normalized, NormalizeValue(rule.Column, selectedValue), StringComparison.Ordinal));
            classification = "Canonical identity";
            resolution = "use_canonical_identity";
            // An empty broadcast_slot is the persisted representation of the
            // canonical STANDARD slot. The canonical row's existence, rather
            // than string non-emptiness, determines whether this is resolved.
            autoResolved = canonical.Exists &&
                (string.Equals(rule.Column, "broadcast_slot", StringComparison.Ordinal) ||
                 !string.IsNullOrWhiteSpace(selectedValue));
            requiresReview = !autoResolved;
            confidence = autoResolved ? 100 : 20;
        }
        else if (normalizedGroups.Length == 1)
        {
            selectedCandidate = nonEmpty.OrderByDescending(x => x.Score).ThenByDescending(x => x.EpisodeId == survivorEpisodeId).ThenBy(x => x.EpisodeId).First();
            if (hasEmpty && authoritativeEmpty)
            {
                selectedValue = string.Empty;
                classification = "Protected empty vs populated";
                resolution = "manual_review";
                autoResolved = false;
                requiresReview = true;
                confidence = 30;
            }
            else
            {
                selectedValue = selectedCandidate.Value;
                classification = hasEmpty ? "Empty vs populated" : "Equivalent formatting";
                resolution = hasEmpty ? "use_populated" : "normalise_equivalent";
                autoResolved = true;
                requiresReview = false;
                confidence = hasEmpty ? 98 : 95;
            }
        }
        else if (rule.Kind == MetadataFieldKind.List)
        {
            var authoritativeLists = nonEmpty.Where(HasFieldLevelAuthority).ToArray();
            if (authoritativeLists.Length > 0)
            {
                selectedCandidate = authoritativeLists.OrderByDescending(x => x.Score).ThenBy(x => x.EpisodeId).First();
                selectedValue = string.Empty;
                classification = "Authoritative list conflict";
                resolution = "manual_review";
                autoResolved = false;
                requiresReview = true;
                confidence = 35;
            }
            else
            {
                var union = nonEmpty.SelectMany(x => SplitList(x.Value)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
                selectedValue = string.Join(", ", union);
                selectedCandidate = null;
                classification = "Mergeable union";
                resolution = "merge_union";
                autoResolved = union.Length > 0;
                requiresReview = !autoResolved;
                confidence = autoResolved ? 94 : 25;
            }
        }
        else
        {
            var representatives = normalizedGroups
                .Select(group => group.OrderByDescending(x => x.Score).ThenByDescending(x => x.EpisodeId == survivorEpisodeId).ThenBy(x => x.EpisodeId).First())
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.EpisodeId == survivorEpisodeId)
                .ThenBy(x => x.EpisodeId)
                .ToArray();
            var hasFieldAuthority = representatives.Any(HasFieldLevelAuthority);
            var handledByFieldPolicy = false;

            if (string.Equals(rule.Column, "broadcast_variant", StringComparison.Ordinal) &&
                !hasFieldAuthority &&
                representatives.All(x => IsRecordingLevelVariant(x.Value)))
            {
                // Multipart part labels and archive-capture labels describe the
                // Recording/Segment layer, not the canonical Broadcast.
                selectedCandidate = null;
                selectedValue = string.Empty;
                classification = "Recording-level variant";
                resolution = "move_to_recording_evidence";
                autoResolved = true;
                requiresReview = false;
                confidence = 99;
                handledByFieldPolicy = true;
            }
            else if (string.Equals(rule.Column, "title", StringComparison.Ordinal) && !hasFieldAuthority)
            {
                var descriptive = representatives.Where(x => !IsFilenameDerivedTitle(x.Value)).ToArray();
                var filenameDerived = representatives.Where(x => IsFilenameDerivedTitle(x.Value)).ToArray();
                if (filenameDerived.Length > 0 && descriptive.Length <= 1)
                {
                    selectedCandidate = descriptive.Length == 1 ? descriptive[0] : null;
                    selectedValue = selectedCandidate?.Value ?? string.Empty;
                    classification = descriptive.Length == 1 ? "Descriptive title over filename" : "Filename-derived titles";
                    resolution = descriptive.Length == 1 ? "prefer_descriptive_title" : "clear_filename_titles";
                    autoResolved = true;
                    requiresReview = false;
                    confidence = descriptive.Length == 1 ? 96 : 98;
                    handledByFieldPolicy = true;
                }
            }
            else if (string.Equals(rule.Column, "artwork_path", StringComparison.Ordinal) && !hasFieldAuthority)
            {
                // Artwork alternatives are assets, not semantic metadata
                // contradictions. Keep the provisional survivor's asset where
                // possible and preserve every alternate path in the ledger.
                selectedCandidate = representatives.FirstOrDefault(x => x.EpisodeId == survivorEpisodeId) ?? representatives[0];
                selectedValue = selectedCandidate.Value;
                classification = "Survivor artwork";
                resolution = "prefer_survivor_asset";
                autoResolved = true;
                requiresReview = false;
                confidence = 90;
                handledByFieldPolicy = true;
            }
            else if (string.Equals(rule.Column, "broadcast_era", StringComparison.Ordinal) && !hasFieldAuthority)
            {
                // Existing era labels are generated descriptive taxonomy. They
                // can be ranked deterministically without asking the user to
                // arbitrate punctuation, station-name breadth or transition
                // wording; all alternates remain preserved.
                selectedCandidate = representatives
                    .OrderByDescending(EraPolicyScore)
                    .ThenByDescending(x => x.EpisodeId == survivorEpisodeId)
                    .ThenBy(x => x.EpisodeId)
                    .First();
                selectedValue = selectedCandidate.Value;
                classification = "Generated era winner";
                resolution = "prefer_specific_generated_era";
                autoResolved = true;
                requiresReview = false;
                confidence = 88;
                handledByFieldPolicy = true;
            }

            if (!handledByFieldPolicy)
            {
                var specific = representatives.Where(x => !IsGenericValue(rule.Column, x.Value)).ToArray();
                var authoritativePlaceholders = representatives.Any(x =>
                    IsGenericValue(rule.Column, x.Value) && HasFieldLevelAuthority(x));
                if (specific.Length == 1 && representatives.Length > 1 && !authoritativePlaceholders)
                {
                    selectedCandidate = specific[0];
                    selectedValue = selectedCandidate.Value;
                    classification = "Specific over placeholder";
                    resolution = "prefer_specific";
                    autoResolved = true;
                    requiresReview = false;
                    confidence = 92;
                }
                else
                {
                    selectedCandidate = representatives[0];
                    var runnerUp = representatives.Length > 1 ? representatives[1] : null;
                    var margin = runnerUp is null ? selectedCandidate.Score : selectedCandidate.Score - runnerUp.Score;
                    var protectedWinner = selectedCandidate.Protected && (runnerUp is null || !runnerUp.Protected);
                    var manualWinner = HasManualProvenance(selectedCandidate) && (runnerUp is null || !HasManualProvenance(runnerUp));
                    var protectedConflict = representatives.Count(x => x.Protected) > 1;
                    var manualConflict = representatives.Count(HasManualProvenance) > 1;
                    // user_modified is episode-wide and cannot veto a
                    // field-level decision when no matching manual/protected
                    // provenance exists for this field.
                    var qualityWinner = margin >= 20 && !protectedConflict && !manualConflict;
                    autoResolved = protectedWinner || manualWinner || qualityWinner;
                    requiresReview = !autoResolved;
                    selectedValue = autoResolved ? selectedCandidate.Value : string.Empty;
                    classification = autoResolved
                        ? protectedWinner ? "Protected provenance winner" : manualWinner ? "Manual edit winner" : "Quality-ranked winner"
                        : "Genuine conflicting values";
                    resolution = autoResolved
                        ? protectedWinner ? "prefer_protected" : manualWinner ? "prefer_manual" : "prefer_higher_quality"
                        : "manual_review";
                    confidence = autoResolved ? Math.Clamp(70 + Math.Max(0, margin), 70, 99) : Math.Clamp(45 - Math.Max(0, margin), 20, 45);
                }
            }
        }

        if (requiresReview) selectedCandidate = null;

        var selectedNormalized = NormalizeValue(rule.Column, selectedValue);
        var preservedAlternates = normalizedGroups.Count(group => !string.Equals(group.Key, selectedNormalized, StringComparison.Ordinal));
        if (selectedNormalized.Length == 0) preservedAlternates = normalizedGroups.Length;
        var candidateJson = JsonSerializer.Serialize(candidates.Select(x => new
        {
            episodeId = x.EpisodeId,
            value = x.Value,
            normalizedValue = x.Normalized,
            score = x.Score,
            userModified = x.UserModified,
            metadataConfidence = x.MetadataConfidence
        }), _json);
        var provenanceJson = JsonSerializer.Serialize(candidates.SelectMany(candidate =>
            candidate.MatchingProvenance.Count > 0
                ? candidate.MatchingProvenance.Select(source => new
                {
                    episodeId = candidate.EpisodeId,
                    value = candidate.Value,
                    sourceKind = source.SourceKind,
                    sourceLabel = source.SourceLabel,
                    provenanceConfidence = source.Confidence,
                    evidenceCount = source.EvidenceCount,
                    protectedValue = source.Protected
                })
                : new[]
                {
                    new
                    {
                        episodeId = candidate.EpisodeId,
                        value = candidate.Value,
                        sourceKind = "system",
                        sourceLabel = string.Empty,
                        provenanceConfidence = 0,
                        evidenceCount = 0,
                        protectedValue = false
                    }
                }), _json);
        var evidence = requiresReview
            ? $"{rule.DisplayName} has {normalizedGroups.Length:N0} materially different non-empty values with no decisive provenance or quality advantage. All values are preserved for review."
            : $"{rule.DisplayName} was classified as {classification.ToLowerInvariant()} and resolved by '{resolution}'. {preservedAlternates:N0} alternate value(s) remain preserved in the forensic ledger.";

        return new ConflictForensicResult(
            canonicalKey, rule.Column, "Episode metadata", classification, selectedCandidate?.EpisodeId,
            selectedValue, candidateJson, provenanceJson, resolution, autoResolved, requiresReview,
            confidence, preservedAlternates, evidence);
    }

    private MetadataCandidate BuildCandidate(
        EpisodeSnapshot snapshot,
        MetadataFieldRule rule,
        IReadOnlyDictionary<string, List<ProvenanceSnapshot>> provenance,
        long survivorEpisodeId)
    {
        var value = snapshot.ValueFor(rule.Column);
        var normalized = NormalizeValue(rule.Column, value);
        var aliases = ProvenanceAliases(rule.Column);
        var possible = aliases
            .SelectMany(alias => provenance.TryGetValue(ProvenanceKey(snapshot.Id, alias), out var values)
                ? values.AsEnumerable()
                : Enumerable.Empty<ProvenanceSnapshot>())
            .ToArray();
        var matching = possible.Where(x => NormalizeValue(rule.Column, x.ValueText) == normalized).ToArray();
        // Provenance only belongs to the candidate when its value actually
        // matches the episode field. A protected/manual provenance row for an
        // older alternate must never lend its authority to a different value.
        var best = matching
            .OrderByDescending(ProvenanceScore)
            .ThenByDescending(x => x.Id)
            .FirstOrDefault();
        var protectedValue = best?.Protected ?? false;
        var sourceKind = best?.SourceKind ?? "system";
        var sourceLabel = best?.SourceLabel ?? string.Empty;
        var provenanceConfidence = best?.Confidence ?? 0;
        var evidenceCount = best?.EvidenceCount ?? 0;
        var score = snapshot.MetadataConfidence;
        // user_modified is episode-wide, not field-specific. Treat it only as
        // weak supporting evidence; decisive manual authority requires matching
        // field-level manual provenance.
        if (snapshot.UserModified) score += 8;
        if (protectedValue) score += 70;
        score += sourceKind switch { "manual" => 45, "research_pack" => 30, "rollback" => 25, _ => 0 };
        score += provenanceConfidence / 2;
        score += Math.Min(20, evidenceCount * 2);
        if (snapshot.Id == survivorEpisodeId) score += 2;
        if (IsGenericValue(rule.Column, value)) score -= 45;
        else score += Math.Min(15, value.Trim().Length / 12);
        return new MetadataCandidate(snapshot.Id, value, normalized, score, snapshot.UserModified,
            snapshot.MetadataConfidence, protectedValue, sourceKind, sourceLabel, provenanceConfidence, evidenceCount,
            matching.OrderByDescending(ProvenanceScore).ThenByDescending(x => x.Id).ToArray());
    }

    private static bool HasManualProvenance(MetadataCandidate candidate)
        => string.Equals(candidate.SourceKind, "manual", StringComparison.OrdinalIgnoreCase);

    private static bool HasFieldLevelAuthority(MetadataCandidate candidate)
        => candidate.Protected || HasManualProvenance(candidate);

    private static int ProvenanceScore(ProvenanceSnapshot value)
        => (value.Protected ? 1000 : 0) + (value.SourceKind switch
        {
            "manual" => 500 + value.Confidence * 2 + value.EvidenceCount,
            "research_pack" => 350 + value.Confidence * 2 + value.EvidenceCount,
            "rollback" => 300 + value.Confidence * 2 + value.EvidenceCount,
            _ => value.Confidence * 2 + value.EvidenceCount
        });

    private static string[] ProvenanceAliases(string field)
        => field switch
        {
            "title" => new[] { "title", "headline" },
            "description" => new[] { "description", "summary" },
            "edition" => new[] { "edition", "station" },
            "research_sources" => new[] { "research_sources", "sources", "source_links" },
            "mentioned_people" => new[] { "mentioned_people", "people" },
            _ => new[] { field }
        };

    private static void ApplySelectedMetadataValue(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long survivorEpisodeId,
        string field,
        string selectedValue)
    {
        if (!MetadataFieldRules.Any(x => string.Equals(x.Column, field, StringComparison.Ordinal)))
            throw new InvalidOperationException($"Unknown metadata field '{field}'.");
        if (field == "collection_id")
        {
            if (!long.TryParse(selectedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var collectionId)) return;
            Execute(connection, transaction, "UPDATE episodes SET collection_id=$value WHERE id=$episode",
                ("$value", collectionId), ("$episode", survivorEpisodeId));
            return;
        }
        Execute(connection, transaction, $"UPDATE episodes SET {field}=$value WHERE id=$episode",
            ("$value", selectedValue), ("$episode", survivorEpisodeId));
    }

    private static List<EpisodeSnapshot> LoadEpisodeSnapshots(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<long> episodeIds)
    {
        var idSql = string.Join(",", episodeIds);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT id,collection_id,COALESCE(air_date,''),COALESCE(title,''),COALESCE(description,''),COALESCE(notes,''),
                   COALESCE(artwork_path,''),COALESCE(broadcast_slot,''),COALESCE(edition,''),COALESCE(archive_notes,''),
                   COALESCE(broadcast_variant,''),COALESCE(broadcast_era,''),COALESCE(episode_type,''),
                   COALESCE(research_sources,''),COALESCE(hosts,''),COALESCE(callers,''),COALESCE(mentioned_people,''),
                   user_modified,metadata_confidence,updated_at
              FROM episodes WHERE id IN ({idSql}) ORDER BY id
            """;
        var result = new List<EpisodeSnapshot>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new EpisodeSnapshot(
                reader.GetInt64(0), reader.GetInt64(1).ToString(CultureInfo.InvariantCulture), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.GetString(9), reader.GetString(10), reader.GetString(11), reader.GetString(12), reader.GetString(13),
                reader.GetString(14), reader.GetString(15), reader.GetString(16), reader.GetInt64(17) != 0, reader.GetInt32(18), reader.GetString(19)));
        }
        return result;
    }

    private static Dictionary<string, List<ProvenanceSnapshot>> LoadProvenance(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<long> episodeIds)
    {
        var idSql = string.Join(",", episodeIds);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT id,episode_id,LOWER(field_name),value_text,source_kind,source_label,confidence,evidence_count,protected
              FROM research_field_provenance
             WHERE active=1 AND episode_id IN ({idSql})
             ORDER BY episode_id,field_name,id
            """;
        var result = new Dictionary<string, List<ProvenanceSnapshot>>(StringComparer.OrdinalIgnoreCase);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var value = new ProvenanceSnapshot(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt32(6), reader.GetInt32(7), reader.GetInt64(8) != 0);
            var key = ProvenanceKey(value.EpisodeId, value.FieldName);
            if (!result.TryGetValue(key, out var values)) result[key] = values = new List<ProvenanceSnapshot>();
            values.Add(value);
        }
        return result;
    }

    private static CanonicalIdentity LoadCanonicalIdentity(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long truthRunId,
        string canonicalKey)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COALESCE(c.id,0),COALESCE(b.air_date,''),COALESCE(b.broadcast_slot,'')
              FROM library_truth_broadcasts b
              LEFT JOIN collections c ON LOWER(c.name)=LOWER(b.collection_name)
             WHERE b.run_id=$run AND b.canonical_key=$key
             LIMIT 1
            """;
        command.Parameters.AddWithValue("$run", truthRunId);
        command.Parameters.AddWithValue("$key", canonicalKey);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new CanonicalIdentity(reader.GetInt64(0).ToString(CultureInfo.InvariantCulture), reader.GetString(1), reader.GetString(2), true)
            : new CanonicalIdentity(string.Empty, string.Empty, string.Empty, false);
    }

    private static string NormalizeValue(string field, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        if (MetadataFieldRules.First(x => x.Column == field).Kind == MetadataFieldKind.List)
            return string.Join("|", SplitList(value).Select(NormalizeText).Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal));
        if (field == "collection_id") return value.Trim();
        if (field == "air_date" && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var date))
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return NormalizeText(value);
    }

    private static string NormalizeText(string value)
    {
        var normalized = value.Trim()
            .Replace('\u2013', '-').Replace('\u2014', '-').Replace('\u2212', '-')
            .Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201c', '"').Replace('\u201d', '"');
        normalized = WhitespaceRegex.Replace(normalized, " ");
        return normalized.Trim(' ', '.', ',', ';', ':', '-', '_').ToLowerInvariant();
    }

    private static IReadOnlyList<string> SplitList(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Array.Empty<string>();
        var trimmed = value.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            try
            {
                var json = JsonSerializer.Deserialize<string[]>(trimmed);
                if (json is not null) return json.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray();
            }
            catch { }
        }
        return trimmed.Split(new[] { ',', ';', '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length > 0)
            .ToArray();
    }

    private static bool IsFilenameDerivedTitle(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return false;
        return FilenamePartTitleRegex.IsMatch(trimmed) || FilenameDateTitleRegex.IsMatch(trimmed);
    }

    private static bool IsRecordingLevelVariant(string value)
        => RecordingLevelVariantRegex.IsMatch(NormalizeText(value));

    private static int EraPolicyScore(MetadataCandidate candidate)
    {
        var normalized = NormalizeText(candidate.Value);
        var score = candidate.Score;
        if (normalized.Contains("transition", StringComparison.Ordinal) ||
            normalized.Contains("final", StringComparison.Ordinal) ||
            normalized.Contains("early", StringComparison.Ordinal) ||
            normalized.Contains("late", StringComparison.Ordinal) ||
            normalized.Contains("dual-show", StringComparison.Ordinal))
            score += 12;
        if (normalized.Contains("wjfk", StringComparison.Ordinal) ||
            normalized.Contains("wnew", StringComparison.Ordinal) ||
            normalized.Contains("virus", StringComparison.Ordinal) ||
            normalized.Contains("opie & anthony", StringComparison.Ordinal) ||
            normalized.Contains("raw dog", StringComparison.Ordinal) ||
            normalized.Contains("xm 202", StringComparison.Ordinal))
            score += 8;
        score += Math.Min(8, normalized.Length / 24);
        return score;
    }

    private static bool IsGenericValue(string field, string value)
    {
        var normalized = NormalizeText(value);
        if (normalized.Length == 0) return true;
        if (field is "title" or "description" or "notes" or "archive_notes")
        {
            if (DateOnlyRegex.IsMatch(normalized)) return true;
            if (normalized is "ron & fez" or "ron and fez" or "bennington" or "afro show" or "opie & anthony" or "opie and anthony") return true;
            if (normalized.Contains("archive broadcast", StringComparison.Ordinal) ||
                normalized.Contains("regular episode", StringComparison.Ordinal) ||
                normalized.Contains("faction talk", StringComparison.Ordinal) ||
                normalized.Contains("radio show archive", StringComparison.Ordinal) ||
                normalized is "episode" or "broadcast") return true;
        }
        return false;
    }

    private static string HeadlineSelectedValue(HeadlineReviewSnapshot row)
        => string.Equals(row.Decision, "Accepted", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(row.ReviewedHeadline)
            ? row.ReviewedHeadline
            : row.Candidate;

    private static int HeadlineScore(HeadlineReviewSnapshot row)
    {
        var score = row.Confidence.Trim().ToLowerInvariant() switch
        {
            "confirmed" => 90,
            "high" => 80,
            "probable" => 65,
            "medium" => 55,
            "low" => 30,
            _ => 45
        };
        score += row.Decision.Trim().ToLowerInvariant() switch
        {
            "accepted" => 60,
            "rejected" => 35,
            "skipped" => 30,
            _ => 0
        };
        if (!IsGenericValue("title", row.Candidate)) score += 15;
        score += Math.Min(10, row.Reasoning.Length / 30);
        return score;
    }

    private static string ReadRowsAsJson(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
                row[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            rows.Add(row);
        }
        return JsonSerializer.Serialize(rows);
    }

    private static string FormatForensicCandidates(string json)
        => FormatForensicJson(json, "episodeId", "value", "score");

    private static string FormatForensicProvenance(string json)
        => FormatForensicJson(json, "episodeId", "sourceKind", "sourceLabel", "provenanceConfidence", "protectedValue");

    private static string FormatForensicJson(string json, params string[] preferredProperties)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return json;
            var lines = new List<string>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                var parts = new List<string>();
                foreach (var property in preferredProperties)
                {
                    if (!element.TryGetProperty(property, out var value)) continue;
                    var text = value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString();
                    if (!string.IsNullOrWhiteSpace(text)) parts.Add($"{property}: {text}");
                }
                if (parts.Count == 0) parts.Add(element.ToString());
                lines.Add(string.Join(" · ", parts));
            }
            return string.Join(Environment.NewLine, lines);
        }
        catch { return json; }
    }

    private static string ProvenanceKey(long episodeId, string fieldName) => $"{episodeId}|{fieldName}";

    private enum MetadataFieldKind { Structural, Text, List }
    private sealed record MetadataFieldRule(string Column, string DisplayName, MetadataFieldKind Kind);
    private sealed record CanonicalIdentity(string CollectionId, string AirDate, string BroadcastSlot, bool Exists)
    {
        public string ValueFor(string field) => field switch
        {
            "collection_id" => CollectionId,
            "air_date" => AirDate,
            "broadcast_slot" => BroadcastSlot,
            _ => string.Empty
        };
    }
    private sealed record EpisodeSnapshot(
        long Id,string CollectionId,string AirDate,string Title,string Description,string Notes,string ArtworkPath,
        string BroadcastSlot,string Edition,string ArchiveNotes,string BroadcastVariant,string BroadcastEra,string EpisodeType,
        string ResearchSources,string Hosts,string Callers,string MentionedPeople,bool UserModified,int MetadataConfidence,string UpdatedAt)
    {
        public string ValueFor(string field) => field switch
        {
            "collection_id" => CollectionId,
            "air_date" => AirDate,
            "title" => Title,
            "description" => Description,
            "notes" => Notes,
            "artwork_path" => ArtworkPath,
            "broadcast_slot" => BroadcastSlot,
            "edition" => Edition,
            "archive_notes" => ArchiveNotes,
            "broadcast_variant" => BroadcastVariant,
            "broadcast_era" => BroadcastEra,
            "episode_type" => EpisodeType,
            "research_sources" => ResearchSources,
            "hosts" => Hosts,
            "callers" => Callers,
            "mentioned_people" => MentionedPeople,
            _ => string.Empty
        };
    }
    private sealed record ProvenanceSnapshot(long Id,long EpisodeId,string FieldName,string ValueText,string SourceKind,string SourceLabel,int Confidence,int EvidenceCount,bool Protected);
    private sealed record MetadataCandidate(long EpisodeId,string Value,string Normalized,int Score,bool UserModified,int MetadataConfidence,bool Protected,string SourceKind,string SourceLabel,int ProvenanceConfidence,int EvidenceCount,IReadOnlyList<ProvenanceSnapshot> MatchingProvenance);
    private sealed record HeadlineReviewSnapshot(long EpisodeId,string Candidate,string ReviewedHeadline,string Confidence,string Reasoning,string Decision,string ParserVersion,string UpdatedAt);
    private sealed record HeadlineAnalysisResult(IReadOnlyList<ConflictForensicResult> Forensics,int StateRowsMigrated);
    private sealed record AliasAnalysisResult(IReadOnlyList<ConflictForensicResult> Forensics,int StateRowsMigrated);
    private sealed record AliasReferenceRule(string Table,string DisplayName);
    private sealed record ConflictForensicResult(
        string CanonicalKey,string FieldName,string ConflictKind,string Classification,long? SelectedEpisodeId,string SelectedValue,
        string CandidateValuesJson,string ProvenanceJson,string Resolution,bool AutoResolved,bool RequiresReview,int ConfidenceScore,
        int PreservedAlternateCount,string Evidence);
}
