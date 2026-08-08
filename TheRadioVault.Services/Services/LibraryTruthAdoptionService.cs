using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Guarded alpha10 live adoption path. It is deliberately coupled to a
/// previously completed and rollback-verified rehearsal. The live transaction
/// must reproduce the rehearsal's per-broadcast operations and field-level
/// policy decisions exactly before SQLite is allowed to commit.
/// </summary>
public sealed partial class LibraryTruthAdoptionRehearsalService
{
    private static readonly string[] AdoptionTargetTables =
    {
        "canonical_broadcasts",
        "recordings",
        "recording_segments",
        "recording_coverages",
        "episode_canonical_map"
    };

    public LibraryTruthAdoptionRunSummary GetLatestAdoptionSummary()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,truth_run_id,rehearsal_run_id,app_version,started_at,completed_at,status,backup_path,
                   source_fingerprint,staged_fingerprint,post_commit_fingerprint,
                   rehearsal_truth_signature,commit_truth_signature,
                   rehearsal_item_signature,commit_item_signature,rehearsal_conflict_signature,commit_conflict_signature,
                   eligible_broadcasts,canonical_writes,recording_writes,segment_writes,coverage_writes,file_reassignments,
                   alias_rows_retired,state_rows_migrated,metadata_conflicts,auto_resolved_conflicts,unresolved_conflicts,
                   preserved_alternates,transcript_conflicts,foreign_key_violations,integrity_check,backup_restore_check,
                   commit_verified,message
              FROM library_truth_adoption_runs
             ORDER BY id DESC LIMIT 1
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAdoptionSummary(reader) : LibraryTruthAdoptionRunSummary.Empty;
    }

    public LibraryTruthAdoptionEligibility GetAdoptionEligibility(
        CancellationToken cancellationToken = default)
    {
        using var connection = _database.OpenConnection();
        var latestAdoption = ReadLatestAdoptionStatus(connection);
        var targetRows = AdoptionTargetRowCount(connection, null);
        if (string.Equals(latestAdoption.Status, "completed", StringComparison.OrdinalIgnoreCase))
            return LibraryTruthAdoptionEligibility.Blocked(
                $"Library Truth was already adopted successfully on {latestAdoption.CompletedDisplay}. The committed plan cannot be run twice.");
        if (string.Equals(latestAdoption.Status, "committed_pending_verification", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(latestAdoption.Status, "verification_failed", StringComparison.OrdinalIgnoreCase))
            return LibraryTruthAdoptionEligibility.Blocked(
                $"A previous adoption reached the commit boundary but has not passed final verification. Close Radio Vault and restore the retained backup before doing anything else: {latestAdoption.BackupPath}");

        var safeTerminalStatus = string.Equals(latestAdoption.Status, "not-run", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(latestAdoption.Status, "failed", StringComparison.OrdinalIgnoreCase) ||
                                 string.Equals(latestAdoption.Status, "cancelled", StringComparison.OrdinalIgnoreCase);
        if (!safeTerminalStatus)
            return LibraryTruthAdoptionEligibility.Blocked(
                $"A previous guarded-adoption attempt is still recorded as ‘{latestAdoption.Status}’. Treat it as interrupted and inspect or restore its retained backup before retrying: {latestAdoption.BackupPath}");
        if (targetRows != 0)
            return LibraryTruthAdoptionEligibility.Blocked(
                $"The committed Library Truth tables already contain {targetRows:N0} row(s), but there is no completed adoption record. Restore or inspect the database before attempting adoption.");

        var rehearsal = ReadLatestCompletedRehearsal(connection);
        if (rehearsal.Id == 0)
            return LibraryTruthAdoptionEligibility.Blocked("Run and complete a sealed adoption rehearsal before enabling live adoption.");
        if (!rehearsal.RollbackVerified ||
            rehearsal.ForeignKeyViolations != 0 ||
            !string.Equals(rehearsal.IntegrityCheck, "ok", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(rehearsal.BackupRestoreCheck, "ok", StringComparison.OrdinalIgnoreCase))
            return LibraryTruthAdoptionEligibility.Blocked("The latest rehearsal did not pass every backup, integrity and rollback guard.");
        if (!IsSha256(rehearsal.TruthRunSignature) ||
            !IsSha256(rehearsal.ItemSignature) ||
            !IsSha256(rehearsal.ConflictSignature))
            return LibraryTruthAdoptionEligibility.Blocked(
                "The latest completed rehearsal predates the current plan-sealing format. Run one fresh rehearsal so the exact shadow plan and both forensic ledgers are cryptographically sealed before live adoption.");

        var latestTruthRunId = Convert.ToInt64(
            Scalar(connection, null, "SELECT COALESCE(MAX(id),0) FROM library_truth_runs WHERE status='completed'"),
            CultureInfo.InvariantCulture);
        if (latestTruthRunId == 0 || latestTruthRunId != rehearsal.TruthRunId)
            return LibraryTruthAdoptionEligibility.Blocked(
                "The latest Library Truth shadow run no longer matches the rollback-verified rehearsal. Run the rehearsal again.");

        var previewCount = Convert.ToInt32(Scalar(connection, null, """
            SELECT COUNT(*) FROM library_truth_adoption_previews
             WHERE run_id=$run AND eligible_for_guarded_adoption=1
            """, ("$run", rehearsal.TruthRunId)), CultureInfo.InvariantCulture);
        var itemCount = Convert.ToInt32(Scalar(connection, null, """
            SELECT COUNT(*) FROM library_truth_rehearsal_items WHERE rehearsal_run_id=$run
            """, ("$run", rehearsal.Id)), CultureInfo.InvariantCulture);
        var conflictCount = Convert.ToInt32(Scalar(connection, null, """
            SELECT COUNT(*) FROM library_truth_rehearsal_conflicts WHERE rehearsal_run_id=$run
            """, ("$run", rehearsal.Id)), CultureInfo.InvariantCulture);
        if (previewCount != rehearsal.EligibleBroadcasts || itemCount != rehearsal.EligibleBroadcasts || conflictCount != rehearsal.MetadataConflicts)
            return LibraryTruthAdoptionEligibility.Blocked(
                "The persisted adoption preview or forensic ledger is incomplete. Rebuild Library Truth and rerun the rehearsal.");

        var currentTruthRunSignature = ComputeTruthRunSignature(connection, null, rehearsal.TruthRunId, cancellationToken);
        var currentItemSignature = ComputePersistedRehearsalItemSignature(connection, null, rehearsal.Id);
        var currentConflictSignature = ComputePersistedRehearsalConflictSignature(connection, null, rehearsal.Id);
        if (!string.Equals(currentTruthRunSignature, rehearsal.TruthRunSignature, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(currentItemSignature, rehearsal.ItemSignature, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(currentConflictSignature, rehearsal.ConflictSignature, StringComparison.OrdinalIgnoreCase))
            return LibraryTruthAdoptionEligibility.Blocked(
                "The sealed Library Truth plan or its forensic ledger has changed since rehearsal. Rebuild Library Truth and run a fresh sealed rehearsal; the edited plan will not be committed.");

        var currentFingerprint = ComputeLogicalFingerprint(connection, cancellationToken);
        if (!string.Equals(currentFingerprint, rehearsal.SourceFingerprint, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(currentFingerprint, rehearsal.RollbackFingerprint, StringComparison.OrdinalIgnoreCase))
            return new LibraryTruthAdoptionEligibility(
                false,
                "The live metadata has changed since the verified rehearsal. Nothing is wrong, but the rehearsal must be rerun against the current database before adoption.",
                rehearsal.TruthRunId,
                rehearsal.Id,
                rehearsal.EligibleBroadcasts,
                rehearsal.CanonicalWrites,
                rehearsal.RecordingWrites,
                rehearsal.SegmentWrites,
                rehearsal.CoverageWrites,
                rehearsal.FileReassignments,
                rehearsal.AliasRowsRetired,
                rehearsal.StateRowsMigrated,
                rehearsal.MetadataConflicts,
                rehearsal.AutoResolvedConflicts,
                rehearsal.UnresolvedConflicts,
                rehearsal.PreservedAlternates,
                rehearsal.TranscriptConflicts,
                rehearsal.SourceFingerprint,
                currentFingerprint,
                rehearsal.TruthRunSignature,
                rehearsal.ItemSignature,
                rehearsal.ConflictSignature,
                rehearsal.CompletedDisplay,
                latestAdoption.Status);

        return new LibraryTruthAdoptionEligibility(
            true,
            "The live database, sealed truth plan and both forensic ledgers exactly match the rollback-verified rehearsal.",
            rehearsal.TruthRunId,
            rehearsal.Id,
            rehearsal.EligibleBroadcasts,
            rehearsal.CanonicalWrites,
            rehearsal.RecordingWrites,
            rehearsal.SegmentWrites,
            rehearsal.CoverageWrites,
            rehearsal.FileReassignments,
            rehearsal.AliasRowsRetired,
            rehearsal.StateRowsMigrated,
            rehearsal.MetadataConflicts,
            rehearsal.AutoResolvedConflicts,
            rehearsal.UnresolvedConflicts,
            rehearsal.PreservedAlternates,
            rehearsal.TranscriptConflicts,
            rehearsal.SourceFingerprint,
            currentFingerprint,
            rehearsal.TruthRunSignature,
            rehearsal.ItemSignature,
            rehearsal.ConflictSignature,
            rehearsal.CompletedDisplay,
            latestAdoption.Status);
    }

    private static bool IsSha256(string value)
        => value.Length == 64 && value.All(Uri.IsHexDigit);

    public LibraryTruthAdoptionRunSummary AdoptVerifiedPlan(
        string backupDirectory,
        string appVersion,
        IProgress<LibraryTruthRehearsalProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(backupDirectory))
            throw new ArgumentException("A backup directory is required.", nameof(backupDirectory));
        if (string.IsNullOrWhiteSpace(appVersion)) appVersion = "unknown";

        Directory.CreateDirectory(backupDirectory);
        var eligibility = GetAdoptionEligibility(cancellationToken);
        if (!eligibility.CanAdopt) throw new InvalidOperationException(eligibility.Reason);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        var backupPath = Path.Combine(backupDirectory, $"RadioVault-before-library-truth-adoption-{stamp}.db");
        long reportId = 0;
        var adoptionVerified = false;

        try
        {
            progress?.Report(new("Backup", 0, 1, "Creating the retained pre-adoption SQLite backup…"));
            CreateOnlineBackup(_database.DatabasePath, backupPath, cancellationToken);
            var backupRestoreCheck = ValidateDatabaseFile(backupPath);
            if (!string.Equals(backupRestoreCheck, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The pre-adoption backup could not be validated: {backupRestoreCheck}");

            var backupFingerprint = ComputeDatabaseFileFingerprint(backupPath, cancellationToken, includeAdoptionTables: false);
            if (!string.Equals(backupFingerprint, eligibility.ExpectedSourceFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The retained backup does not match the rollback-verified rehearsal fingerprint.");
            progress?.Report(new("Backup", 1, 1, "Backup reopened successfully and matches the rehearsed source fingerprint."));

            reportId = BeginAdoptionReport(
                eligibility.TruthRunId,
                eligibility.RehearsalRunId,
                appVersion,
                backupPath,
                backupFingerprint,
                eligibility.ExpectedTruthRunSignature);

            ExecuteLiveAdoption(
                reportId,
                eligibility,
                backupPath,
                backupFingerprint,
                backupRestoreCheck,
                progress,
                cancellationToken);
            adoptionVerified = true;
            return GetAdoptionSummary(reportId);
        }
        catch (LibraryTruthAdoptionCommitBoundaryException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            if (reportId != 0)
                TryFailAdoptionReport(reportId, "cancelled", "Library Truth adoption was cancelled before commit. The live library was not changed.");
            throw;
        }
        catch (Exception ex)
        {
            if (adoptionVerified)
            {
                throw new LibraryTruthAdoptionCommitBoundaryException(
                    $"Library Truth committed and passed independent verification, but its final summary could not be reopened: {ex.Message}. Radio Vault must close and reopen against the adopted database.",
                    backupPath,
                    ex,
                    restoreRequired: false);
            }

            if (reportId != 0)
                TryFailAdoptionReport(reportId, "failed", $"Library Truth adoption failed safely before commit: {ex.Message}. The retained backup was not deleted.");
            throw;
        }
    }

    private void ExecuteLiveAdoption(
        long reportId,
        LibraryTruthAdoptionEligibility eligibility,
        string backupPath,
        string backupFingerprint,
        string backupRestoreCheck,
        IProgress<LibraryTruthRehearsalProgress>? progress,
        CancellationToken cancellationToken)
    {
        RehearsalExecution? execution = null;
        string stagedFingerprint;
        string rehearsalTruthSignature;
        string commitTruthSignature;
        string rehearsalItemSignature;
        string commitItemSignature;
        string rehearsalConflictSignature;
        string commitConflictSignature;
        var committed = false;

        var commitAttempted = false;
        try
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();

            // This first write acquires SQLite's write reservation before the
            // source fingerprint is checked again, preventing playback or any
            // other app surface from changing the plan underneath the commit.
            Execute(connection, transaction,
                "UPDATE library_truth_adoption_runs SET status='validating',message='Validating the live database against the rehearsed plan.' WHERE id=$id",
                ("$id", reportId));

            cancellationToken.ThrowIfCancellationRequested();
            var currentFingerprint = ComputeLogicalFingerprint(connection, cancellationToken, transaction);
            if (!string.Equals(currentFingerprint, eligibility.ExpectedSourceFingerprint, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(currentFingerprint, backupFingerprint, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The live database changed after the backup was created. Adoption stopped before any live migration was committed.");

            rehearsalTruthSignature = ComputeTruthRunSignature(connection, transaction, eligibility.TruthRunId, cancellationToken);
            rehearsalItemSignature = ComputePersistedRehearsalItemSignature(connection, transaction, eligibility.RehearsalRunId);
            rehearsalConflictSignature = ComputePersistedRehearsalConflictSignature(connection, transaction, eligibility.RehearsalRunId);
            if (!string.Equals(rehearsalTruthSignature, eligibility.ExpectedTruthRunSignature, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(rehearsalItemSignature, eligibility.ExpectedItemSignature, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(rehearsalConflictSignature, eligibility.ExpectedConflictSignature, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "The sealed truth plan or forensic ledger changed after final eligibility was checked. Adoption stopped before commit.");

            EnsureAdoptionTargetsEmpty(connection, transaction);
            var previews = LoadEligiblePreviews(connection, eligibility.TruthRunId, transaction);
            if (previews.Count != eligibility.EligibleBroadcasts)
                throw new InvalidOperationException(
                    $"The guarded preview changed: expected {eligibility.EligibleBroadcasts:N0} broadcasts but loaded {previews.Count:N0}.");

            CreateRehearsalSchema(connection, transaction);
            execution = ExecutePlansInsideTransaction(
                connection,
                transaction,
                eligibility.TruthRunId,
                previews,
                progress,
                cancellationToken);

            AssertExecutionMatchesRehearsal(execution, eligibility);
            commitTruthSignature = ComputeTruthRunSignature(connection, transaction, eligibility.TruthRunId, cancellationToken);
            if (!string.Equals(commitTruthSignature, eligibility.ExpectedTruthRunSignature, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The exact shadow truth plan changed while the live transaction was being staged.");

            commitItemSignature = ComputeExecutionItemSignature(execution.Items);
            if (!string.Equals(rehearsalItemSignature, commitItemSignature, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(commitItemSignature, eligibility.ExpectedItemSignature, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The per-broadcast live operation plan did not exactly reproduce the sealed rehearsal ledger.");

            commitConflictSignature = ComputeExecutionConflictSignature(execution.Conflicts);
            if (!string.Equals(rehearsalConflictSignature, commitConflictSignature, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(commitConflictSignature, eligibility.ExpectedConflictSignature, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The live metadata policy decisions did not exactly reproduce the sealed rehearsal forensic ledger.");

            progress?.Report(new("Commit validation", 0, 1, "Copying the rehearsed canonical structure into the permanent Library Truth tables…"));
            CopyRehearsalStructureToAdoptedTables(connection, transaction, eligibility.TruthRunId);
            AssertAdoptedStructureMatchesExecution(connection, transaction, execution);
            DropRehearsalSchema(connection, transaction);
            PersistAdoptionItems(connection, transaction, reportId, execution.Items);
            PersistAdoptionConflicts(connection, transaction, reportId, execution.Conflicts);
            AssertAdoptionAuditMatchesExecution(connection, transaction, reportId, execution);

            var foreignKeyViolations = ScalarInt(connection, transaction, "SELECT COUNT(*) FROM pragma_foreign_key_check");
            var integrityCheck = Convert.ToString(Scalar(connection, transaction, "PRAGMA integrity_check"), CultureInfo.InvariantCulture) ?? "unknown";
            if (foreignKeyViolations != 0 || !string.Equals(integrityCheck, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"The staged live migration failed integrity validation: {foreignKeyViolations} foreign-key violation(s), integrity={integrityCheck}.");

            stagedFingerprint = ComputeLogicalFingerprint(connection, cancellationToken, transaction, includeAdoptionTables: true);
            MarkAdoptionPendingVerification(
                connection,
                transaction,
                reportId,
                backupRestoreCheck,
                stagedFingerprint,
                rehearsalTruthSignature,
                commitTruthSignature,
                rehearsalItemSignature,
                commitItemSignature,
                rehearsalConflictSignature,
                commitConflictSignature,
                execution,
                foreignKeyViolations,
                integrityCheck);

            cancellationToken.ThrowIfCancellationRequested();
            commitAttempted = true;
            transaction.Commit();
            committed = true;
            progress?.Report(new("Commit validation", 1, 1, "The single SQLite transaction committed. Running independent post-commit verification…"));
        }
        catch (LibraryTruthAdoptionCommitBoundaryException)
        {
            throw;
        }
        catch (Exception ex) when (commitAttempted)
        {
            throw new LibraryTruthAdoptionCommitBoundaryException(
                $"SQLite reached the commit boundary but did not return cleanly: {ex.Message}. Close Radio Vault and restore the retained backup before reopening.",
                backupPath,
                ex);
        }

        if (!committed || execution is null)
            throw new InvalidOperationException("The adoption transaction did not reach commit.");

        try
        {
            using var verificationConnection = _database.OpenConnection();
            var postCommitFingerprint = ComputeLogicalFingerprint(
                verificationConnection,
                CancellationToken.None,
                includeAdoptionTables: true);
            var postCommitTruthSignature = ComputeTruthRunSignature(
                verificationConnection, null, eligibility.TruthRunId, CancellationToken.None);
            var postCommitItemSignature = ComputePersistedRehearsalItemSignature(
                verificationConnection, null, eligibility.RehearsalRunId);
            var postCommitConflictSignature = ComputePersistedRehearsalConflictSignature(
                verificationConnection, null, eligibility.RehearsalRunId);
            var sealedPlanMatches = string.Equals(postCommitTruthSignature, eligibility.ExpectedTruthRunSignature, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(postCommitItemSignature, eligibility.ExpectedItemSignature, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(postCommitConflictSignature, eligibility.ExpectedConflictSignature, StringComparison.OrdinalIgnoreCase);
            var foreignKeyViolations = ScalarInt(verificationConnection, null, "SELECT COUNT(*) FROM pragma_foreign_key_check");
            var integrityCheck = Convert.ToString(Scalar(verificationConnection, null, "PRAGMA integrity_check"), CultureInfo.InvariantCulture) ?? "unknown";
            var structureMatches = AdoptedStructureMatchesExecution(verificationConnection, null, execution, out var structureDisplay);
            var auditMatches = AdoptionAuditMatchesExecution(verificationConnection, null, reportId, execution, out var auditDisplay);

            if (!string.Equals(postCommitFingerprint, stagedFingerprint, StringComparison.OrdinalIgnoreCase) ||
                foreignKeyViolations != 0 ||
                !string.Equals(integrityCheck, "ok", StringComparison.OrdinalIgnoreCase) ||
                !sealedPlanMatches ||
                !structureMatches ||
                !auditMatches)
            {
                var failure = $"Post-commit verification failed: fingerprint match={string.Equals(postCommitFingerprint, stagedFingerprint, StringComparison.OrdinalIgnoreCase)}, " +
                              $"foreign keys={foreignKeyViolations}, integrity={integrityCheck}, sealed plan={sealedPlanMatches}, structure={structureDisplay}, audit={auditDisplay}. " +
                              $"Close Radio Vault and restore {backupPath}.";
                MarkAdoptionVerificationFailure(reportId, postCommitFingerprint, foreignKeyViolations, integrityCheck, failure);
                throw new InvalidOperationException(failure);
            }

            CompleteAdoptionReport(reportId, postCommitFingerprint, foreignKeyViolations, integrityCheck, execution);
            progress?.Report(new("Completed", 1, 1, "Live adoption and independent post-commit verification completed successfully."));
        }
        catch (Exception ex)
        {
            var status = TryGetAdoptionStatus(reportId);
            if (string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
                return;

            if (!string.Equals(status, "verification_failed", StringComparison.OrdinalIgnoreCase))
            {
                TryMarkAdoptionVerificationFailure(
                    reportId,
                    string.Empty,
                    -1,
                    "verification exception",
                    $"The migration committed but final verification could not complete. Close Radio Vault and restore {backupPath} before continuing.");
            }

            throw new LibraryTruthAdoptionCommitBoundaryException(
                $"The Library Truth transaction committed, but independent verification did not complete successfully: {ex.Message}. Close Radio Vault and restore the retained backup before reopening.",
                backupPath,
                ex);
        }
    }

    private RehearsalExecution ExecutePlansInsideTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long truthRunId,
        IReadOnlyList<PreviewPlan> previews,
        IProgress<LibraryTruthRehearsalProgress>? progress,
        CancellationToken cancellationToken)
    {
        var items = new List<RehearsalItemResult>(previews.Count);
        var conflicts = new List<ConflictForensicResult>();
        var canonicalWrites = 0;
        var recordingWrites = 0;
        var segmentWrites = 0;
        var coverageWrites = 0;
        var fileReassignments = 0;
        var aliasesRetired = 0;
        var stateRows = 0;
        var metadataConflicts = 0;
        var autoResolved = 0;
        var unresolved = 0;
        var alternates = 0;
        var transcriptConflicts = 0;

        for (var index = 0; index < previews.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var preview = previews[index];
            if (index == 0 || index % 25 == 0 || index == previews.Count - 1)
                progress?.Report(new("Live adoption", index, previews.Count, $"Applying the rehearsed plan for {preview.CanonicalKey}…"));

            var item = RehearseBroadcast(connection, transaction, truthRunId, preview);
            items.Add(item);
            canonicalWrites += item.CanonicalWrites;
            recordingWrites += item.RecordingWrites;
            segmentWrites += item.SegmentWrites;
            coverageWrites += item.CoverageWrites;
            fileReassignments += item.FilesReassigned;
            aliasesRetired += item.AliasesRetired;
            stateRows += item.StateRowsMigrated;
            metadataConflicts += item.MetadataConflicts;
            autoResolved += item.AutoResolvedConflicts;
            unresolved += item.UnresolvedConflicts;
            alternates += item.PreservedAlternates;
            transcriptConflicts += item.TranscriptConflicts;
            conflicts.AddRange(item.Conflicts);
        }

        return new RehearsalExecution(
            previews.Count,
            canonicalWrites,
            recordingWrites,
            segmentWrites,
            coverageWrites,
            fileReassignments,
            aliasesRetired,
            stateRows,
            metadataConflicts,
            autoResolved,
            unresolved,
            alternates,
            transcriptConflicts,
            0,
            "pending",
            string.Empty,
            string.Empty,
            false,
            items,
            conflicts);
    }

    private static void AssertExecutionMatchesRehearsal(
        RehearsalExecution execution,
        LibraryTruthAdoptionEligibility expected)
    {
        var mismatch = new List<string>();
        if (execution.EligibleBroadcasts != expected.EligibleBroadcasts) mismatch.Add($"broadcasts {execution.EligibleBroadcasts}/{expected.EligibleBroadcasts}");
        if (execution.CanonicalWrites != expected.CanonicalWrites) mismatch.Add($"canonical {execution.CanonicalWrites}/{expected.CanonicalWrites}");
        if (execution.RecordingWrites != expected.RecordingWrites) mismatch.Add($"recordings {execution.RecordingWrites}/{expected.RecordingWrites}");
        if (execution.SegmentWrites != expected.SegmentWrites) mismatch.Add($"segments {execution.SegmentWrites}/{expected.SegmentWrites}");
        if (execution.CoverageWrites != expected.CoverageWrites) mismatch.Add($"coverages {execution.CoverageWrites}/{expected.CoverageWrites}");
        if (execution.FileReassignments != expected.FileReassignments) mismatch.Add($"file links {execution.FileReassignments}/{expected.FileReassignments}");
        if (execution.AliasRowsRetired != expected.AliasRowsRetired) mismatch.Add($"aliases {execution.AliasRowsRetired}/{expected.AliasRowsRetired}");
        if (execution.StateRowsMigrated != expected.StateRowsMigrated) mismatch.Add($"state rows {execution.StateRowsMigrated}/{expected.StateRowsMigrated}");
        if (execution.MetadataConflicts != expected.MetadataConflicts) mismatch.Add($"forensics {execution.MetadataConflicts}/{expected.MetadataConflicts}");
        if (execution.AutoResolvedConflicts != expected.AutoResolvedConflicts) mismatch.Add($"auto-resolved {execution.AutoResolvedConflicts}/{expected.AutoResolvedConflicts}");
        if (execution.UnresolvedConflicts != expected.UnresolvedConflicts) mismatch.Add($"unresolved {execution.UnresolvedConflicts}/{expected.UnresolvedConflicts}");
        if (execution.PreservedAlternates != expected.PreservedAlternates) mismatch.Add($"alternates {execution.PreservedAlternates}/{expected.PreservedAlternates}");
        if (execution.TranscriptConflicts != expected.TranscriptConflicts) mismatch.Add($"transcripts {execution.TranscriptConflicts}/{expected.TranscriptConflicts}");
        if (mismatch.Count > 0)
            throw new InvalidOperationException("The live transaction diverged from the verified rehearsal: " + string.Join(", ", mismatch) + ".");
    }

    private static void EnsureAdoptionTargetsEmpty(SqliteConnection connection, SqliteTransaction transaction)
    {
        var rows = AdoptionTargetRowCount(connection, transaction);
        if (rows != 0)
            throw new InvalidOperationException($"The permanent Library Truth tables are not empty ({rows:N0} row(s)). Adoption cannot be applied twice.");
    }

    private static int AdoptionTargetRowCount(SqliteConnection connection, SqliteTransaction? transaction)
    {
        var total = 0;
        foreach (var table in AdoptionTargetTables)
            total += ScalarInt(connection, transaction, $"SELECT COUNT(*) FROM {table}");
        return total;
    }

    private static void CopyRehearsalStructureToAdoptedTables(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long truthRunId)
    {
        var adoptedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        Execute(connection, transaction, """
            INSERT INTO canonical_broadcasts(
                canonical_key,collection_name,air_date,broadcast_slot,preferred_recording_key,confidence_score,
                source_truth_run_id,adopted_at)
            SELECT canonical_key,collection_name,air_date,broadcast_slot,preferred_recording_key,confidence_score,
                   source_truth_run_id,$adopted
              FROM rehearsal_canonical_broadcasts
            """, ("$adopted", adoptedAt));
        Execute(connection, transaction, """
            INSERT INTO recordings(
                recording_key,canonical_key,label,duration_ms,role,completeness_score,preferred_score,is_preferred,
                source_truth_run_id,adopted_at)
            SELECT recording_key,canonical_key,label,duration_ms,role,completeness_score,preferred_score,is_preferred,
                   $run,$adopted
              FROM rehearsal_recordings
            """, ("$run", truthRunId), ("$adopted", adoptedAt));
        Execute(connection, transaction, """
            INSERT INTO recording_segments(
                recording_key,segment_number,segment_total,start_offset_ms,end_offset_ms,media_file_ids_json,
                source_truth_run_id,adopted_at)
            SELECT recording_key,segment_number,segment_total,start_offset_ms,end_offset_ms,media_file_ids_json,
                   $run,$adopted
              FROM rehearsal_recording_segments
            """, ("$run", truthRunId), ("$adopted", adoptedAt));
        Execute(connection, transaction, """
            INSERT INTO recording_coverages(
                recording_key,segment_number,target_canonical_key,coverage_kind,start_offset_ms,end_offset_ms,
                confidence_score,requires_review,evidence,source_truth_run_id,adopted_at)
            SELECT recording_key,segment_number,target_canonical_key,coverage_kind,start_offset_ms,end_offset_ms,
                   confidence_score,requires_review,evidence,$run,$adopted
              FROM rehearsal_recording_coverages
            """, ("$run", truthRunId), ("$adopted", adoptedAt));
        Execute(connection, transaction, """
            INSERT INTO episode_canonical_map(
                episode_id,canonical_key,survivor_episode_id,is_survivor,source_truth_run_id,adopted_at)
            SELECT episode_id,canonical_key,survivor_episode_id,is_survivor,$run,$adopted
              FROM rehearsal_episode_canonical_map
            """, ("$run", truthRunId), ("$adopted", adoptedAt));
    }

    private static void AssertAdoptedStructureMatchesExecution(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RehearsalExecution execution)
    {
        if (!AdoptedStructureMatchesExecution(connection, transaction, execution, out var display))
            throw new InvalidOperationException($"The permanent Library Truth row counts did not match the verified transaction: {display}.");
    }

    private static bool AdoptedStructureMatchesExecution(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        RehearsalExecution execution,
        out string display)
    {
        var expectedEpisodeMaps = execution.Items.Sum(item => item.AliasEpisodeIds.Count + 1);
        var canonical = ScalarInt(connection, transaction, "SELECT COUNT(*) FROM canonical_broadcasts");
        var recordings = ScalarInt(connection, transaction, "SELECT COUNT(*) FROM recordings");
        var segments = ScalarInt(connection, transaction, "SELECT COUNT(*) FROM recording_segments");
        var coverages = ScalarInt(connection, transaction, "SELECT COUNT(*) FROM recording_coverages");
        var maps = ScalarInt(connection, transaction, "SELECT COUNT(*) FROM episode_canonical_map");
        display = $"canonical {canonical:N0}/{execution.CanonicalWrites:N0}, recordings {recordings:N0}/{execution.RecordingWrites:N0}, " +
                  $"segments {segments:N0}/{execution.SegmentWrites:N0}, coverages {coverages:N0}/{execution.CoverageWrites:N0}, " +
                  $"episode maps {maps:N0}/{expectedEpisodeMaps:N0}";
        return canonical == execution.CanonicalWrites &&
               recordings == execution.RecordingWrites &&
               segments == execution.SegmentWrites &&
               coverages == execution.CoverageWrites &&
               maps == expectedEpisodeMaps;
    }

    private static void AssertAdoptionAuditMatchesExecution(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long reportId,
        RehearsalExecution execution)
    {
        if (!AdoptionAuditMatchesExecution(connection, transaction, reportId, execution, out var display))
            throw new InvalidOperationException($"The permanent adoption audit did not match the verified transaction: {display}.");
    }

    private static bool AdoptionAuditMatchesExecution(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long reportId,
        RehearsalExecution execution,
        out string display)
    {
        var items = Convert.ToInt32(Scalar(connection, transaction,
            "SELECT COUNT(*) FROM library_truth_adoption_items WHERE adoption_run_id=$run", ("$run", reportId)), CultureInfo.InvariantCulture);
        var conflicts = Convert.ToInt32(Scalar(connection, transaction,
            "SELECT COUNT(*) FROM library_truth_adoption_conflicts WHERE adoption_run_id=$run", ("$run", reportId)), CultureInfo.InvariantCulture);
        display = $"items {items:N0}/{execution.Items.Count:N0}, conflicts {conflicts:N0}/{execution.Conflicts.Count:N0}";
        return items == execution.Items.Count && conflicts == execution.Conflicts.Count;
    }

    private static void DropRehearsalSchema(SqliteConnection connection, SqliteTransaction transaction)
        => Execute(connection, transaction, """
            DROP TABLE rehearsal_episode_canonical_map;
            DROP TABLE rehearsal_recording_coverages;
            DROP TABLE rehearsal_recording_segments;
            DROP TABLE rehearsal_recordings;
            DROP TABLE rehearsal_canonical_broadcasts;
            """);

    private static string ComputeTruthRunSignature(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long truthRunId,
        CancellationToken cancellationToken)
    {
        var sources = new (string Name, string Sql)[]
        {
            ("library_truth_runs", "SELECT * FROM library_truth_runs WHERE id=$run ORDER BY id"),
            ("library_truth_files", "SELECT * FROM library_truth_files WHERE run_id=$run ORDER BY id"),
            ("library_truth_recordings", "SELECT * FROM library_truth_recordings WHERE run_id=$run ORDER BY id"),
            ("library_truth_broadcasts", "SELECT * FROM library_truth_broadcasts WHERE run_id=$run ORDER BY id"),
            ("library_truth_coverages", "SELECT * FROM library_truth_coverages WHERE run_id=$run ORDER BY id"),
            ("library_truth_adoption_previews", "SELECT * FROM library_truth_adoption_previews WHERE run_id=$run ORDER BY id")
        };

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var runRows = 0;
        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AppendSignatureValue(hash, source.Name);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = source.Sql;
            command.Parameters.AddWithValue("$run", truthRunId);
            using var reader = command.ExecuteReader();
            var rows = 0;
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                AppendReaderRow(hash, reader);
                rows++;
            }
            AppendSignatureValue(hash, rows);
            if (string.Equals(source.Name, "library_truth_runs", StringComparison.Ordinal))
                runRows = rows;
        }

        if (runRows != 1)
            throw new InvalidOperationException($"Library Truth run {truthRunId:N0} could not be sealed because its run row was missing or duplicated.");
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeDatabaseFileFingerprint(
        string path,
        CancellationToken cancellationToken,
        bool includeAdoptionTables)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return ComputeLogicalFingerprint(connection, cancellationToken, includeAdoptionTables: includeAdoptionTables);
    }

    private static string ComputePersistedRehearsalItemSignature(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long rehearsalRunId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT canonical_key,survivor_episode_id,alias_episode_ids_json,files_reassigned,state_rows_migrated,
                   metadata_conflicts,auto_resolved_conflicts,unresolved_conflicts,preserved_alternates,
                   transcript_conflicts,outcome
              FROM library_truth_rehearsal_items
             WHERE rehearsal_run_id=$run
             ORDER BY canonical_key
            """;
        command.Parameters.AddWithValue("$run", rehearsalRunId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            AppendReaderRow(hash, reader);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeExecutionItemSignature(IReadOnlyList<RehearsalItemResult> items)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var item in items.OrderBy(x => x.CanonicalKey, StringComparer.Ordinal))
        {
            AppendSignatureValue(hash, item.CanonicalKey);
            AppendSignatureValue(hash, item.SurvivorEpisodeId);
            AppendSignatureValue(hash, JsonSerializer.Serialize(item.AliasEpisodeIds));
            AppendSignatureValue(hash, item.FilesReassigned);
            AppendSignatureValue(hash, item.StateRowsMigrated);
            AppendSignatureValue(hash, item.MetadataConflicts);
            AppendSignatureValue(hash, item.AutoResolvedConflicts);
            AppendSignatureValue(hash, item.UnresolvedConflicts);
            AppendSignatureValue(hash, item.PreservedAlternates);
            AppendSignatureValue(hash, item.TranscriptConflicts);
            AppendSignatureValue(hash, item.Outcome);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputePersistedRehearsalConflictSignature(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        long rehearsalRunId)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT canonical_key,field_name,conflict_kind,classification,selected_episode_id,selected_value,
                   candidate_values_json,provenance_json,resolution,auto_resolved,requires_review,
                   confidence_score,preserved_alternate_count
              FROM library_truth_rehearsal_conflicts
             WHERE rehearsal_run_id=$run
             ORDER BY canonical_key,field_name,id
            """;
        command.Parameters.AddWithValue("$run", rehearsalRunId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
            AppendReaderRow(hash, reader);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputeExecutionConflictSignature(IReadOnlyList<ConflictForensicResult> conflicts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var conflict in conflicts
                     .Select((value, index) => new { value, index })
                     .OrderBy(x => x.value.CanonicalKey, StringComparer.Ordinal)
                     .ThenBy(x => x.value.FieldName, StringComparer.Ordinal)
                     .ThenBy(x => x.index))
        {
            var value = conflict.value;
            AppendSignatureValue(hash, value.CanonicalKey);
            AppendSignatureValue(hash, value.FieldName);
            AppendSignatureValue(hash, value.ConflictKind);
            AppendSignatureValue(hash, value.Classification);
            AppendSignatureValue(hash, value.SelectedEpisodeId);
            AppendSignatureValue(hash, value.SelectedValue);
            AppendSignatureValue(hash, value.CandidateValuesJson);
            AppendSignatureValue(hash, value.ProvenanceJson);
            AppendSignatureValue(hash, value.Resolution);
            AppendSignatureValue(hash, value.AutoResolved ? 1 : 0);
            AppendSignatureValue(hash, value.RequiresReview ? 1 : 0);
            AppendSignatureValue(hash, value.ConfidenceScore);
            AppendSignatureValue(hash, value.PreservedAlternateCount);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendReaderRow(IncrementalHash hash, SqliteDataReader reader)
    {
        for (var index = 0; index < reader.FieldCount; index++)
            AppendSignatureValue(hash, reader.IsDBNull(index) ? null : reader.GetValue(index));
    }

    private static void AppendSignatureValue(IncrementalHash hash, object? value)
    {
        var text = value is null or DBNull
            ? "<null>"
            : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        var bytes = Encoding.UTF8.GetBytes(text);
        Append(hash, bytes.Length.ToString(CultureInfo.InvariantCulture));
        Append(hash, ":");
        hash.AppendData(bytes);
        Append(hash, ";");
    }

    private static void PersistAdoptionItems(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long reportId,
        IReadOnlyList<RehearsalItemResult> items)
    {
        foreach (var item in items)
        {
            var evidence = item.Evidence
                .Replace("Disposable transaction", "Live adoption transaction", StringComparison.Ordinal)
                .Replace("Every change is rolled back.", "The change was committed only after matching the verified rehearsal.", StringComparison.Ordinal);
            Execute(connection, transaction, """
                INSERT INTO library_truth_adoption_items(
                    adoption_run_id,canonical_key,survivor_episode_id,alias_episode_ids_json,files_reassigned,state_rows_migrated,
                    metadata_conflicts,auto_resolved_conflicts,unresolved_conflicts,preserved_alternates,
                    transcript_conflicts,outcome,evidence)
                VALUES($run,$key,$survivor,$aliases,$files,$state,$metadata,$auto,$unresolved,$alternates,$transcripts,$outcome,$evidence)
                """,
                ("$run", reportId),
                ("$key", item.CanonicalKey),
                ("$survivor", item.SurvivorEpisodeId),
                ("$aliases", JsonSerializer.Serialize(item.AliasEpisodeIds)),
                ("$files", item.FilesReassigned),
                ("$state", item.StateRowsMigrated),
                ("$metadata", item.MetadataConflicts),
                ("$auto", item.AutoResolvedConflicts),
                ("$unresolved", item.UnresolvedConflicts),
                ("$alternates", item.PreservedAlternates),
                ("$transcripts", item.TranscriptConflicts),
                ("$outcome", item.UnresolvedConflicts > 0 || item.TranscriptConflicts > 0 ? "Committed with review" : "Committed"),
                ("$evidence", evidence));
        }
    }

    private static void PersistAdoptionConflicts(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long reportId,
        IReadOnlyList<ConflictForensicResult> conflicts)
    {
        foreach (var conflict in conflicts)
        {
            Execute(connection, transaction, """
                INSERT INTO library_truth_adoption_conflicts(
                    adoption_run_id,canonical_key,field_name,conflict_kind,classification,selected_episode_id,
                    selected_value,candidate_values_json,provenance_json,resolution,auto_resolved,requires_review,
                    confidence_score,preserved_alternate_count,evidence)
                VALUES($run,$key,$field,$kind,$classification,$selectedEpisode,$selectedValue,$candidates,$provenance,
                       $resolution,$auto,$review,$confidence,$alternates,$evidence)
                """,
                ("$run", reportId),
                ("$key", conflict.CanonicalKey),
                ("$field", conflict.FieldName),
                ("$kind", conflict.ConflictKind),
                ("$classification", conflict.Classification),
                ("$selectedEpisode", conflict.SelectedEpisodeId),
                ("$selectedValue", conflict.SelectedValue),
                ("$candidates", conflict.CandidateValuesJson),
                ("$provenance", conflict.ProvenanceJson),
                ("$resolution", conflict.Resolution),
                ("$auto", conflict.AutoResolved ? 1 : 0),
                ("$review", conflict.RequiresReview ? 1 : 0),
                ("$confidence", conflict.ConfidenceScore),
                ("$alternates", conflict.PreservedAlternateCount),
                ("$evidence", conflict.Evidence));
        }
    }

    private long BeginAdoptionReport(
        long truthRunId,
        long rehearsalRunId,
        string appVersion,
        string backupPath,
        string sourceFingerprint,
        string rehearsalTruthSignature)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO library_truth_adoption_runs(
                truth_run_id,rehearsal_run_id,app_version,started_at,status,backup_path,source_fingerprint,
                rehearsal_truth_signature,message)
            VALUES($truth,$rehearsal,$version,$started,'running',$backup,$fingerprint,$truthSignature,
                   'Preparing guarded live Library Truth adoption from the sealed plan.');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$truth", truthRunId);
        command.Parameters.AddWithValue("$rehearsal", rehearsalRunId);
        command.Parameters.AddWithValue("$version", appVersion);
        command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$backup", backupPath);
        command.Parameters.AddWithValue("$fingerprint", sourceFingerprint);
        command.Parameters.AddWithValue("$truthSignature", rehearsalTruthSignature);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void MarkAdoptionPendingVerification(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long reportId,
        string backupRestoreCheck,
        string stagedFingerprint,
        string rehearsalTruthSignature,
        string commitTruthSignature,
        string rehearsalItemSignature,
        string commitItemSignature,
        string rehearsalConflictSignature,
        string commitConflictSignature,
        RehearsalExecution result,
        int foreignKeyViolations,
        string integrityCheck)
    {
        Execute(connection, transaction, """
            UPDATE library_truth_adoption_runs SET
                status='committed_pending_verification',staged_fingerprint=$staged,
                rehearsal_truth_signature=$rehearsalTruth,commit_truth_signature=$commitTruth,
                rehearsal_item_signature=$rehearsalItems,commit_item_signature=$commitItems,
                rehearsal_conflict_signature=$rehearsalConflicts,commit_conflict_signature=$commitConflicts,
                eligible_broadcasts=$eligible,canonical_writes=$canonical,recording_writes=$recordings,
                segment_writes=$segments,coverage_writes=$coverages,file_reassignments=$files,
                alias_rows_retired=$aliases,state_rows_migrated=$state,metadata_conflicts=$metadata,
                auto_resolved_conflicts=$auto,unresolved_conflicts=$unresolved,preserved_alternates=$alternates,
                transcript_conflicts=$transcripts,foreign_key_violations=$fk,integrity_check=$integrity,
                backup_restore_check=$backupCheck,message=$message
             WHERE id=$id
            """,
            ("$staged", stagedFingerprint),
            ("$rehearsalTruth", rehearsalTruthSignature),
            ("$commitTruth", commitTruthSignature),
            ("$rehearsalItems", rehearsalItemSignature),
            ("$commitItems", commitItemSignature),
            ("$rehearsalConflicts", rehearsalConflictSignature),
            ("$commitConflicts", commitConflictSignature),
            ("$eligible", result.EligibleBroadcasts),
            ("$canonical", result.CanonicalWrites),
            ("$recordings", result.RecordingWrites),
            ("$segments", result.SegmentWrites),
            ("$coverages", result.CoverageWrites),
            ("$files", result.FileReassignments),
            ("$aliases", result.AliasRowsRetired),
            ("$state", result.StateRowsMigrated),
            ("$metadata", result.MetadataConflicts),
            ("$auto", result.AutoResolvedConflicts),
            ("$unresolved", result.UnresolvedConflicts),
            ("$alternates", result.PreservedAlternates),
            ("$transcripts", result.TranscriptConflicts),
            ("$fk", foreignKeyViolations),
            ("$integrity", integrityCheck),
            ("$backupCheck", backupRestoreCheck),
            ("$message", "The rehearsed transaction committed atomically and is awaiting independent post-commit verification."),
            ("$id", reportId));
    }

    private void CompleteAdoptionReport(
        long reportId,
        string postCommitFingerprint,
        int foreignKeyViolations,
        string integrityCheck,
        RehearsalExecution result)
    {
        using var connection = _database.OpenConnection();
        Execute(connection, null, """
            UPDATE library_truth_adoption_runs SET
                completed_at=$completed,status='completed',post_commit_fingerprint=$post,
                foreign_key_violations=$fk,integrity_check=$integrity,commit_verified=1,message=$message
             WHERE id=$id
            """,
            ("$completed", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            ("$post", postCommitFingerprint),
            ("$fk", foreignKeyViolations),
            ("$integrity", integrityCheck),
            ("$message", $"Guarded Library Truth adoption completed for {result.EligibleBroadcasts:N0} broadcasts. The live transaction exactly reproduced the verified rehearsal, retained {result.UnresolvedConflicts:N0} choices for review, and passed independent foreign-key, integrity and fingerprint verification. The pre-adoption backup was retained."),
            ("$id", reportId));
    }

    private void MarkAdoptionVerificationFailure(
        long reportId,
        string postCommitFingerprint,
        int foreignKeyViolations,
        string integrityCheck,
        string message)
    {
        using var connection = _database.OpenConnection();
        Execute(connection, null, """
            UPDATE library_truth_adoption_runs SET
                completed_at=$completed,status='verification_failed',post_commit_fingerprint=$post,
                foreign_key_violations=$fk,integrity_check=$integrity,commit_verified=0,message=$message
             WHERE id=$id
            """,
            ("$completed", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            ("$post", postCommitFingerprint),
            ("$fk", foreignKeyViolations),
            ("$integrity", integrityCheck),
            ("$message", message),
            ("$id", reportId));
    }

    private void TryMarkAdoptionVerificationFailure(
        long reportId,
        string postCommitFingerprint,
        int foreignKeyViolations,
        string integrityCheck,
        string message)
    {
        try
        {
            MarkAdoptionVerificationFailure(reportId, postCommitFingerprint, foreignKeyViolations, integrityCheck, message);
        }
        catch
        {
            // The caller still throws a commit-boundary exception carrying the
            // retained backup path. A damaged/unavailable audit table must never
            // make the UI treat a possibly committed migration as pre-commit.
        }
    }

    private void TryFailAdoptionReport(long reportId, string status, string message)
    {
        try
        {
            FailAdoptionReport(reportId, status, message);
        }
        catch
        {
            // Preserve the original pre-commit failure. The transaction itself
            // has already rolled back or was never started.
        }
    }

    private void FailAdoptionReport(long reportId, string status, string message)
    {
        using var connection = _database.OpenConnection();
        Execute(connection, null, """
            UPDATE library_truth_adoption_runs
               SET completed_at=$completed,status=$status,message=$message
             WHERE id=$id
            """,
            ("$completed", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)),
            ("$status", status),
            ("$message", message),
            ("$id", reportId));
    }

    private LibraryTruthAdoptionRunSummary GetAdoptionSummary(long reportId)
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,truth_run_id,rehearsal_run_id,app_version,started_at,completed_at,status,backup_path,
                   source_fingerprint,staged_fingerprint,post_commit_fingerprint,
                   rehearsal_truth_signature,commit_truth_signature,
                   rehearsal_item_signature,commit_item_signature,rehearsal_conflict_signature,commit_conflict_signature,
                   eligible_broadcasts,canonical_writes,recording_writes,segment_writes,coverage_writes,file_reassignments,
                   alias_rows_retired,state_rows_migrated,metadata_conflicts,auto_resolved_conflicts,unresolved_conflicts,
                   preserved_alternates,transcript_conflicts,foreign_key_violations,integrity_check,backup_restore_check,
                   commit_verified,message
              FROM library_truth_adoption_runs WHERE id=$id
            """;
        command.Parameters.AddWithValue("$id", reportId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadAdoptionSummary(reader) : LibraryTruthAdoptionRunSummary.Empty;
    }

    private static LibraryTruthAdoptionRunSummary ReadAdoptionSummary(SqliteDataReader reader)
        => new(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            reader.IsDBNull(5) ? null : DateTimeOffset.Parse(reader.GetString(5), CultureInfo.InvariantCulture),
            reader.GetString(6),
            reader.GetString(7),
            reader.GetString(8),
            reader.GetString(9),
            reader.GetString(10),
            reader.GetString(11),
            reader.GetString(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetString(15),
            reader.GetString(16),
            reader.GetInt32(17),
            reader.GetInt32(18),
            reader.GetInt32(19),
            reader.GetInt32(20),
            reader.GetInt32(21),
            reader.GetInt32(22),
            reader.GetInt32(23),
            reader.GetInt32(24),
            reader.GetInt32(25),
            reader.GetInt32(26),
            reader.GetInt32(27),
            reader.GetInt32(28),
            reader.GetInt32(29),
            reader.GetInt32(30),
            reader.GetString(31),
            reader.GetString(32),
            reader.GetInt64(33) != 0,
            reader.GetString(34));

    private static LibraryTruthRehearsalSummary ReadLatestCompletedRehearsal(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,truth_run_id,started_at,completed_at,status,backup_path,source_fingerprint,rollback_fingerprint,
                   truth_run_signature,item_signature,conflict_signature,
                   eligible_broadcasts,canonical_writes,recording_writes,segment_writes,coverage_writes,file_reassignments,
                   alias_rows_retired,state_rows_migrated,metadata_conflicts,auto_resolved_conflicts,unresolved_conflicts,preserved_alternates,
                   transcript_conflicts,foreign_key_violations,integrity_check,backup_restore_check,rollback_verified,message
              FROM library_truth_rehearsal_runs
             WHERE status='completed'
             ORDER BY id DESC LIMIT 1
            """;
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSummary(reader) : LibraryTruthRehearsalSummary.Empty;
    }

    private static (string Status, string CompletedDisplay, string BackupPath) ReadLatestAdoptionStatus(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status,completed_at,backup_path FROM library_truth_adoption_runs ORDER BY id DESC LIMIT 1";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return ("not-run", string.Empty, string.Empty);
        var completed = reader.IsDBNull(1)
            ? "an unknown time"
            : DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture).ToLocalTime().ToString("g");
        return (reader.GetString(0), completed, reader.GetString(2));
    }

    private string TryGetAdoptionStatus(long reportId)
    {
        try
        {
            using var connection = _database.OpenConnection();
            return Convert.ToString(
                Scalar(connection, null, "SELECT status FROM library_truth_adoption_runs WHERE id=$id", ("$id", reportId)),
                CultureInfo.InvariantCulture) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

}
