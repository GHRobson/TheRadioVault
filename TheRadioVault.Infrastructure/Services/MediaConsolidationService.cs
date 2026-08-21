using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Models;
using TheRadioVault.Services.Services;

namespace TheRadioVault.Services;

/// <summary>
/// Builds and executes an explicitly confirmed, non-destructive physical-media
/// consolidation. Managed files are verified copies. Every original is moved
/// into a plan-specific quarantine and this service has no media deletion API.
/// </summary>
public sealed class MediaConsolidationService
{
    private const long MinimumFreeReserveBytes = 512L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly SqliteDatabase _database;

    public MediaConsolidationService(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public MediaConsolidationPlan CreatePlan(
        string managedRoot,
        string quarantineRoot,
        IProgress<MediaConsolidationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var managed = NormalizeRoot(managedRoot, nameof(managedRoot));
        var quarantine = NormalizeRoot(quarantineRoot, nameof(quarantineRoot));
        ValidateRootRelationship(managed, quarantine);
        ValidateOutsideRegisteredRoots(managed, quarantine);
        ValidateNoDestinationLinks(managed);
        ValidateNoDestinationLinks(quarantine);

        var runId = LatestCompletedTruthRun();
        if (runId <= 0)
            runId = BuildFreshArchiveReconciliation(
                "No archive reconciliation exists yet. Building the first non-destructive snapshot…",
                progress,
                cancellationToken);

        var inventory = ReadInventorySnapshot(runId);
        if (inventory.HiddenAvailableOutsideTruthFiles > 0)
            EnsureTruthCoversCurrentInventory(inventory, runId);
        if (inventory.AvailableOutsideTruthFiles > 0)
        {
            runId = BuildFreshArchiveReconciliation(
                $"The archive changed after reconciliation run {runId:N0}. Building a fresh non-destructive snapshot…",
                progress,
                cancellationToken);
            inventory = ReadInventorySnapshot(runId);
        }
        EnsureTruthCoversCurrentInventory(inventory, runId);
        var raw = ReadCandidates(runId);
        if (raw.Count == 0)
            throw new InvalidOperationException("The latest archive reconciliation contains no available media files.");
        if (raw.Count != inventory.TruthCoveredAvailableFiles)
            throw new InvalidOperationException(
                $"Media consolidation stopped because archive reconciliation {runId:N0} returned {raw.Count:N0} candidate row(s), " +
                $"but the current inventory reconciliation found {inventory.TruthCoveredAvailableFiles:N0}. No plan was created.");

        var planId = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8];
        var warnings = new List<string>();
        var items = new List<MediaConsolidationPlanItem>();
        var eligibleBroadcasts = 0;
        var heldBroadcasts = 0;
        var heldSourceFiles = 0;
        var completedFiles = 0;

        foreach (var broadcast in raw.GroupBy(candidate => candidate.CanonicalKey, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = broadcast.ToArray();
            var first = candidates[0];
            if (!IsSafeAdoptionState(first.AdoptionState) || first.AirDate is null ||
                candidates.Any(candidate => string.IsNullOrWhiteSpace(candidate.RecordingKey)))
            {
                heldBroadcasts++;
                heldSourceFiles += candidates.Length;
                warnings.Add($"Held {first.CanonicalKey}: {HeldReason(first)}");
                continue;
            }

            var inspected = new List<Candidate>(candidates.Length);
            var unsafeGroup = false;
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new(
                    "Fingerprinting",
                    completedFiles,
                    raw.Count,
                    candidate.SourcePath,
                    $"Reading a full identity for {Path.GetFileName(candidate.SourcePath)}"));
                if (!File.Exists(candidate.SourcePath))
                {
                    warnings.Add($"Held {first.CanonicalKey}: a source file is unavailable ({Path.GetFileName(candidate.SourcePath)}).");
                    unsafeGroup = true;
                    break;
                }
                if ((File.GetAttributes(candidate.SourcePath) & FileAttributes.ReparsePoint) != 0)
                {
                    warnings.Add($"Held {first.CanonicalKey}: symbolic-link media is not moved automatically ({Path.GetFileName(candidate.SourcePath)}).");
                    unsafeGroup = true;
                    break;
                }

                var file = new FileInfo(candidate.SourcePath);
                if (file.Length != candidate.SourceBytes)
                {
                    warnings.Add($"Held {first.CanonicalKey}: {Path.GetFileName(candidate.SourcePath)} changed after archive reconciliation ran.");
                    unsafeGroup = true;
                    break;
                }

                var hash = ComputeSha256(candidate.SourcePath, cancellationToken);
                if (!string.IsNullOrWhiteSpace(candidate.StoredFullSha256) &&
                    !hash.Equals(candidate.StoredFullSha256, StringComparison.OrdinalIgnoreCase))
                {
                    warnings.Add($"Held {first.CanonicalKey}: the saved full-file identity no longer matches {Path.GetFileName(candidate.SourcePath)}.");
                    unsafeGroup = true;
                    break;
                }

                inspected.Add(candidate with
                {
                    FullSha256 = hash,
                    EstimatedBitrate = EstimateBitrate(candidate.SourceBytes, candidate.DurationMs)
                });
                completedFiles++;
            }

            if (unsafeGroup)
            {
                heldBroadcasts++;
                heldSourceFiles += candidates.Length;
                continue;
            }

            var variants = inspected
                .GroupBy(candidate => candidate.RecordingKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => BuildVariant(group.Key, group.ToArray()))
                .ToArray();
            if (variants.Any(variant => !variant.IsInternallySafe))
            {
                heldBroadcasts++;
                heldSourceFiles += candidates.Length;
                warnings.Add($"Held {first.CanonicalKey}: a proposed recording segment contains non-identical files. Run archive reconciliation again or review that recording manually.");
                continue;
            }
            if (variants.Length > 1 && variants.Any(variant => variant.DurationMs <= 0))
            {
                heldBroadcasts++;
                heldSourceFiles += candidates.Length;
                warnings.Add($"Held {first.CanonicalKey}: alternate recordings cannot be ranked safely until every runtime is known.");
                continue;
            }

            var selectedVariant = variants
                .OrderByDescending(variant => variant.DurationSecondBucket)
                .ThenByDescending(variant => variant.EstimatedBitrate)
                .ThenByDescending(variant => variant.PreferredScore)
                .ThenBy(variant => variant.ContentIdentity, StringComparer.Ordinal)
                .First();
            eligibleBroadcasts++;

            var selectedMediaIds = new HashSet<long>();
            foreach (var segment in selectedVariant.Candidates.GroupBy(candidate => Math.Max(1, candidate.ProposedPart)))
            {
                var selected = segment
                    .OrderByDescending(candidate => candidate.EstimatedBitrate)
                    .ThenByDescending(candidate => candidate.SourceBytes)
                    .ThenBy(candidate => candidate.FullSha256, StringComparer.Ordinal)
                    .ThenBy(candidate => candidate.MediaFileId)
                    .First();
                selectedMediaIds.Add(selected.MediaFileId);
            }
            var selectedBySegment = selectedVariant.Candidates
                .Where(candidate => selectedMediaIds.Contains(candidate.MediaFileId))
                .ToDictionary(candidate => Math.Max(1, candidate.ProposedPart));
            var selectedSegmentCount = selectedBySegment.Count;

            foreach (var candidate in inspected.OrderBy(candidate => candidate.MediaFileId))
            {
                var selected = selectedMediaIds.Contains(candidate.MediaFileId);
                var hasSelectedSegment = selectedBySegment.TryGetValue(
                    Math.Max(1, candidate.ProposedPart),
                    out var selectedForSegment);
                var disposition = selected
                    ? MediaConsolidationDisposition.ManagedCopy
                    : hasSelectedSegment && candidate.FullSha256.Equals(selectedForSegment!.FullSha256, StringComparison.OrdinalIgnoreCase)
                        ? MediaConsolidationDisposition.RejectedDuplicate
                        : MediaConsolidationDisposition.RejectedAlternate;
                var managedPath = selected
                    ? BuildManagedPath(managed, candidate, selectedSegmentCount)
                    : string.Empty;
                var quarantinePath = BuildQuarantinePath(quarantine, planId, candidate, disposition);
                var reason = selected
                    ? $"Selected recording: {FormatDuration(selectedVariant.DurationMs)}; estimated {FormatBitrate(selectedVariant.EstimatedBitrate)}. Longest runtime wins; equal-length recordings use the highest estimated bitrate."
                    : disposition == MediaConsolidationDisposition.RejectedDuplicate
                        ? "Exact full-file duplicate of the selected segment."
                        : $"Alternate recording ranked behind {selectedVariant.RecordingKey}: {FormatDuration(candidate.RecordingDurationMs)}; estimated {FormatBitrate(candidate.EstimatedBitrate)}.";

                items.Add(new(
                    BuildItemId(candidate),
                    candidate.MediaFileId,
                    candidate.EpisodeId,
                    candidate.CanonicalKey,
                    candidate.RecordingKey,
                    Math.Max(1, candidate.ProposedPart),
                    candidate.ProposedTotalParts,
                    candidate.ShowName,
                    candidate.AirDate!.Value,
                    candidate.BroadcastSlot,
                    candidate.Title,
                    candidate.SourcePath,
                    candidate.SourceBytes,
                    candidate.DurationMs,
                    candidate.EstimatedBitrate,
                    candidate.FullSha256,
                    disposition,
                    managedPath,
                    quarantinePath,
                    reason));
            }
        }

        if (items.Count == 0)
            throw new InvalidOperationException("Archive reconciliation did not find any safely consolidatable broadcasts. Review the held items before moving physical media.");

        EnsureUniqueTargets(items);
        EnsureUniqueSources(items);
        var finalInventory = ReadInventorySnapshot(runId);
        if (!inventory.Signature.Equals(finalInventory.Signature, StringComparison.Ordinal) ||
            inventory.TotalMediaRecords != finalInventory.TotalMediaRecords ||
            inventory.AvailableFiles != finalInventory.AvailableFiles ||
            inventory.MissingFiles != finalInventory.MissingFiles)
            throw new InvalidOperationException(
                "The physical-media inventory changed while the consolidation plan was being prepared. No plan was created; run archive reconciliation again after scanning has settled.");
        if (items.Count + heldSourceFiles != inventory.AvailableFiles)
            throw new InvalidOperationException(
                $"Media consolidation could account for only {items.Count + heldSourceFiles:N0} of {inventory.AvailableFiles:N0} available physical files. No plan was created.");
        var unsigned = new MediaConsolidationPlan(
            planId,
            DateTimeOffset.UtcNow,
            runId,
            managed,
            quarantine,
            string.Empty,
            items,
            eligibleBroadcasts,
            heldBroadcasts,
            inventory.TotalMediaRecords,
            inventory.AvailableFiles,
            inventory.MissingFiles,
            heldSourceFiles,
            inventory.Signature,
            items.Where(item => item.IsManagedCopy).Sum(item => item.SourceBytes),
            items.Sum(item => item.SourceBytes),
            warnings);
        return unsigned with { PlanSignature = ComputePlanSignature(unsigned) };
    }

    public MediaConsolidationPlan? LoadLatestInterruptedPlan(string quarantineRoot)
    {
        var quarantine = NormalizeRoot(quarantineRoot, nameof(quarantineRoot));
        var plansRoot = Path.Combine(quarantine, "RadioVault-Consolidation");
        if (!Directory.Exists(plansRoot)) return null;

        foreach (var directory in Directory.EnumerateDirectories(plansRoot)
                     .OrderByDescending(path => path, StringComparer.Ordinal))
        {
            var journalPath = Path.Combine(directory, "journal.json");
            var manifestPath = Path.Combine(directory, "plan.json");
            if (!File.Exists(journalPath) || !File.Exists(manifestPath)) continue;
            var journal = ReadJournal(journalPath);
            if (journal is null || journal.Status.Equals("completed", StringComparison.OrdinalIgnoreCase)) continue;
            MediaConsolidationPlan plan;
            try
            {
                plan = JsonSerializer.Deserialize<MediaConsolidationPlan>(File.ReadAllText(manifestPath), JsonOptions)
                       ?? throw new InvalidDataException("The interrupted consolidation manifest is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The interrupted consolidation manifest is damaged.", exception);
            }
            ValidatePlanSignature(plan);
            if (!PathsEqual(plan.QuarantineRoot, quarantine) ||
                !journal.PlanId.Equals(plan.PlanId, StringComparison.Ordinal) ||
                !journal.PlanSignature.Equals(plan.PlanSignature, StringComparison.Ordinal))
                throw new InvalidDataException("The interrupted consolidation journal does not match its signed plan.");
            EnsureUniqueTargets(plan.Items);
            EnsureUniqueSources(plan.Items);
            return plan;
        }
        return null;
    }

    public MediaConsolidationRehearsalResult Rehearse(
        MediaConsolidationPlan plan,
        IProgress<MediaConsolidationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlanSignature(plan);
        ValidateRootRelationship(plan.ManagedRoot, plan.QuarantineRoot);
        ValidateOutsideRegisteredRoots(plan.ManagedRoot, plan.QuarantineRoot, allowExactManagedRoot: true);
        ValidateNoDestinationLinks(plan.ManagedRoot);
        ValidateNoDestinationLinks(plan.QuarantineRoot);
        EnsureUniqueTargets(plan.Items);
        EnsureUniqueSources(plan.Items);

        var problems = new List<string>();
        if (!IsCompletedPlan(plan))
        {
            var currentInventory = ReadInventorySnapshot(plan.LibraryTruthRunId);
            if (!plan.InventorySignature.Equals(currentInventory.Signature, StringComparison.Ordinal) ||
                plan.InventoryMediaRecords != currentInventory.TotalMediaRecords ||
                plan.InventoryAvailableFiles != currentInventory.AvailableFiles ||
                plan.InventoryMissingFiles != currentInventory.MissingFiles)
                problems.Add(
                    "The physical-media inventory changed after this plan was prepared. Prepare a new consolidation plan; Radio Vault will refresh archive reconciliation automatically.");
        }
        if (plan.AccountedAvailableFiles != plan.InventoryAvailableFiles)
            problems.Add(
                $"The signed plan accounts for {plan.AccountedAvailableFiles:N0} of {plan.InventoryAvailableFiles:N0} available physical files.");
        long verifiedBytes = 0;
        var verifiedFiles = 0;
        for (var index = 0; index < plan.Items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = plan.Items[index];
            progress?.Report(new("Rehearsal", index, plan.Items.Count, item.SourcePath,
                $"Verifying {Path.GetFileName(item.SourcePath)}"));
            if (!IsWithin(item.QuarantinePath, plan.QuarantineRoot) ||
                (item.IsManagedCopy && !IsWithin(item.ManagedPath, plan.ManagedRoot)))
            {
                problems.Add($"A planned destination escapes its selected root: {item.ItemId}.");
                continue;
            }
            ValidateNoDestinationLinks(item.QuarantinePath);
            if (item.IsManagedCopy) ValidateNoDestinationLinks(item.ManagedPath);

            var availablePath = File.Exists(item.SourcePath)
                ? item.SourcePath
                : File.Exists(item.QuarantinePath) ? item.QuarantinePath : string.Empty;
            if (string.IsNullOrWhiteSpace(availablePath))
            {
                problems.Add($"Source file unavailable: {Path.GetFileName(item.SourcePath)}.");
                continue;
            }

            var info = new FileInfo(availablePath);
            if (info.Length != item.SourceBytes ||
                !ComputeSha256(availablePath, cancellationToken).Equals(item.FullSha256, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"Source identity changed: {Path.GetFileName(item.SourcePath)}.");
                continue;
            }

            if (item.IsManagedCopy && File.Exists(item.ManagedPath) &&
                !ComputeSha256(item.ManagedPath, cancellationToken).Equals(item.FullSha256, StringComparison.OrdinalIgnoreCase))
                problems.Add($"Managed destination already contains different data: {item.ManagedPath}.");
            if (File.Exists(item.QuarantinePath) &&
                !ComputeSha256(item.QuarantinePath, cancellationToken).Equals(item.FullSha256, StringComparison.OrdinalIgnoreCase))
                problems.Add($"Quarantine destination already contains different data: {item.QuarantinePath}.");

            verifiedFiles++;
            verifiedBytes += item.SourceBytes;
        }

        var managedFree = GetAvailableBytes(plan.ManagedRoot);
        var quarantineFree = GetAvailableBytes(plan.QuarantineRoot);
        var managedRequired = plan.Items
            .Where(item => item.IsManagedCopy && !File.Exists(item.ManagedPath))
            .Sum(item => item.SourceBytes);
        var quarantineRequired = plan.Items
            .Where(item => !File.Exists(item.QuarantinePath) &&
                           !SameStorageVolume(item.SourcePath, plan.QuarantineRoot))
            .Sum(item => item.SourceBytes);
        if (managedFree >= 0 && managedRequired + MinimumFreeReserveBytes > managedFree)
            problems.Add($"The managed destination needs {FormatBytes(managedRequired + MinimumFreeReserveBytes)} free including its safety reserve, but only {FormatBytes(managedFree)} is available.");
        if (quarantineFree >= 0 && quarantineRequired + MinimumFreeReserveBytes > quarantineFree)
            problems.Add($"The quarantine needs {FormatBytes(quarantineRequired + MinimumFreeReserveBytes)} free for cross-volume moves including its safety reserve, but only {FormatBytes(quarantineFree)} is available.");

        EnsureWritableDirectory(plan.ManagedRoot);
        EnsureWritableDirectory(plan.QuarantineRoot);
        var planDirectory = PlanDirectory(plan);
        Directory.CreateDirectory(planDirectory);
        var manifestPath = Path.Combine(planDirectory, "plan.json");
        WriteJsonAtomically(manifestPath, plan);
        WriteSafetyNotice(planDirectory, plan);

        var canCommit = problems.Count == 0 && verifiedFiles == plan.Items.Count;
        return new(
            plan.PlanId,
            plan.PlanSignature,
            canCommit,
            verifiedFiles,
            verifiedBytes,
            managedRequired,
            quarantineRequired,
            managedFree,
            quarantineFree,
            manifestPath,
            canCommit
                ? $"Rehearsal passed for {verifiedFiles:N0} files. No media was moved. Stop the server and enter the exact confirmation phrase to commit."
                : $"Rehearsal held the plan with {problems.Count:N0} safety problem(s). No media was moved.",
            problems);
    }

    public MediaConsolidationCommitResult Commit(
        MediaConsolidationPlan plan,
        MediaConsolidationRehearsalResult rehearsal,
        string confirmationText,
        IProgress<MediaConsolidationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(rehearsal);
        ValidatePlanSignature(plan);
        if (!rehearsal.CanCommit || rehearsal.PlanId != plan.PlanId || rehearsal.PlanSignature != plan.PlanSignature)
            throw new InvalidOperationException("This exact consolidation plan has not passed rehearsal.");
        if (!string.Equals(confirmationText?.Trim(), plan.ConfirmationText, StringComparison.Ordinal))
            throw new InvalidOperationException($"Enter the exact confirmation phrase: {plan.ConfirmationText}");

        var finalRehearsal = Rehearse(plan, progress, cancellationToken);
        if (!finalRehearsal.CanCommit)
            throw new InvalidOperationException("The source files changed after rehearsal. Nothing was committed.");

        var planDirectory = PlanDirectory(plan);
        Directory.CreateDirectory(planDirectory);
        var journalPath = Path.Combine(planDirectory, "journal.json");
        var existingJournal = ReadJournal(journalPath);
        if (existingJournal is not null)
            ValidateJournal(existingJournal, plan);
        var backupPath = Path.Combine(planDirectory, "database-before-consolidation.sqlite");
        CreateDatabaseBackup(backupPath, cancellationToken);
        var journal = existingJournal ?? new ConsolidationJournal
        {
            PlanId = plan.PlanId,
            PlanSignature = plan.PlanSignature,
            StartedAt = DateTimeOffset.UtcNow,
            Status = "copying-managed-files"
        };
        WriteJsonAtomically(journalPath, journal);

        var managedCount = 0;
        var managedItems = plan.Items.Where(item => item.IsManagedCopy).ToArray();
        for (var index = 0; index < managedItems.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = managedItems[index];
            progress?.Report(new("Copying", index, managedItems.Length, item.SourcePath,
                $"Creating verified managed copy {Path.GetFileName(item.ManagedPath)}"));
            EnsureVerifiedManagedCopy(item, plan.PlanId, cancellationToken);
            journal.ManagedItemIds.Add(item.ItemId);
            journal.Status = "copying-managed-files";
            WriteJsonAtomically(journalPath, journal);
            managedCount++;
        }

        journal.Status = "quarantining-originals";
        WriteJsonAtomically(journalPath, journal);
        var quarantinedCount = 0;
        for (var index = 0; index < plan.Items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = plan.Items[index];
            progress?.Report(new("Quarantining", index, plan.Items.Count, item.SourcePath,
                $"Moving original to quarantine: {Path.GetFileName(item.SourcePath)}"));
            EnsureOriginalInQuarantine(item, cancellationToken);
            journal.QuarantinedItemIds.Add(item.ItemId);
            journal.Status = "quarantining-originals";
            WriteJsonAtomically(journalPath, journal);
            quarantinedCount++;
        }

        journal.Status = "updating-database";
        WriteJsonAtomically(journalPath, journal);
        var rows = UpdateDatabase(plan);
        journal.Status = "completed";
        journal.CompletedAt = DateTimeOffset.UtcNow;
        journal.DatabaseRowsUpdated = rows;
        WriteJsonAtomically(journalPath, journal);

        progress?.Report(new("Complete", plan.Items.Count, plan.Items.Count, string.Empty,
            "Consolidation completed. Every original remains in quarantine."));
        return new(
            plan.PlanId,
            true,
            managedCount,
            quarantinedCount,
            rows,
            plan.ManagedRoot,
            planDirectory,
            backupPath,
            journalPath,
            $"Consolidated {managedCount:N0} managed file(s). {quarantinedCount:N0} original file(s) remain in quarantine; Radio Vault did not delete them.");
    }

    private IReadOnlyList<Candidate> ReadCandidates(long runId)
    {
        var result = new List<Candidate>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT f.media_file_id,f.current_episode_id,f.path,mf.file_size,COALESCE(mf.duration_ms,0),
                   COALESCE(mf.full_hash,''),f.canonical_broadcast_key,f.recording_key,
                   f.proposed_collection,f.proposed_air_date,COALESCE(f.proposed_slot,''),
                   MAX(1,COALESCE(f.proposed_part,1)),f.proposed_total_parts,COALESCE(f.proposed_headline,''),
                   b.adoption_state,COALESCE(r.duration_ms,0),COALESCE(r.preferred_score,0)
              FROM library_truth_files f
              JOIN media_files mf ON mf.id=f.media_file_id
              JOIN library_truth_broadcasts b
                ON b.run_id=f.run_id AND b.canonical_key=f.canonical_broadcast_key
              LEFT JOIN library_truth_recordings r
                ON r.run_id=f.run_id AND r.recording_key=f.recording_key
             WHERE f.run_id=$run AND COALESCE(mf.is_missing,0)=0
             ORDER BY f.canonical_broadcast_key,f.recording_key,f.proposed_part,f.media_file_id
            """;
        command.Parameters.AddWithValue("$run", runId);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new Candidate(
                reader.GetInt64(0),
                reader.GetInt64(1),
                Path.GetFullPath(reader.GetString(2)),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetString(5).Trim().ToLowerInvariant(),
                string.Empty,
                reader.GetString(6),
                reader.GetString(7),
                reader.GetString(8),
                reader.IsDBNull(9) ? null : DateOnly.Parse(reader.GetString(9)),
                reader.GetString(10),
                reader.GetInt32(11),
                reader.IsDBNull(12) ? null : reader.GetInt32(12),
                reader.GetString(13),
                reader.GetString(14),
                reader.GetInt64(15),
                reader.GetInt32(16),
                0));
        }
        return result;
    }

    private long LatestCompletedTruthRun()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(id),0) FROM library_truth_runs WHERE status='completed'";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private long BuildFreshArchiveReconciliation(
        string initialMessage,
        IProgress<MediaConsolidationProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new(
            "Reconciling inventory",
            0,
            100,
            string.Empty,
            initialMessage));
        var truthProgress = progress is null
            ? null
            : new SynchronousProgress<(double Percent, string Message)>(value => progress.Report(new(
                "Reconciling inventory",
                (int)Math.Round(value.Percent),
                100,
                string.Empty,
                value.Message)));
        var refreshed = new LibraryTruthEngine(_database)
            .BuildShadowIndex(truthProgress, cancellationToken)
            .Summary;
        if (!refreshed.Status.Equals("completed", StringComparison.OrdinalIgnoreCase) || refreshed.RunId <= 0)
            throw new InvalidOperationException("The fresh archive reconciliation did not complete. No consolidation plan was created.");
        return refreshed.RunId;
    }

    private MediaInventorySnapshot ReadInventorySnapshot(long truthRunId)
    {
        using var connection = _database.OpenConnection();
        using var counts = connection.CreateCommand();
        counts.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN COALESCE(mf.is_missing,0)=0 THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN COALESCE(mf.is_missing,0)<>0 THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN COALESCE(mf.is_missing,0)=0 AND EXISTS(
                       SELECT 1 FROM library_truth_files f
                        WHERE f.run_id=$run AND f.media_file_id=mf.id
                   ) THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN COALESCE(mf.is_missing,0)=0 AND NOT EXISTS(
                       SELECT 1 FROM library_truth_files f
                        WHERE f.run_id=$run AND f.media_file_id=mf.id
                   ) THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN COALESCE(mf.is_missing,0)=0 AND COALESCE(e.hidden,0)=0 AND NOT EXISTS(
                       SELECT 1 FROM library_truth_files f
                        WHERE f.run_id=$run AND f.media_file_id=mf.id
                   ) THEN 1 ELSE 0 END),0),
                   COALESCE(SUM(CASE WHEN COALESCE(mf.is_missing,0)=0 AND COALESCE(e.hidden,0)<>0 AND NOT EXISTS(
                       SELECT 1 FROM library_truth_files f
                        WHERE f.run_id=$run AND f.media_file_id=mf.id
                   ) THEN 1 ELSE 0 END),0)
              FROM media_files mf
              JOIN episodes e ON e.id=mf.episode_id
            """;
        counts.Parameters.AddWithValue("$run", truthRunId);
        using var reader = counts.ExecuteReader();
        if (!reader.Read()) throw new InvalidOperationException("The physical-media inventory could not be counted.");
        var total = reader.GetInt32(0);
        var available = reader.GetInt32(1);
        var missing = reader.GetInt32(2);
        var covered = reader.GetInt32(3);
        var outside = reader.GetInt32(4);
        var visibleOutside = reader.GetInt32(5);
        var hiddenOutside = reader.GetInt32(6);
        reader.Close();

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var rows = connection.CreateCommand();
        rows.CommandText = """
            SELECT mf.id,mf.episode_id,mf.path,mf.file_size,mf.modified_time,
                   COALESCE(mf.is_missing,0),COALESCE(mf.storage_state,''),COALESCE(e.hidden,0)
              FROM media_files mf
              JOIN episodes e ON e.id=mf.episode_id
             ORDER BY mf.id
            """;
        using var rowReader = rows.ExecuteReader();
        while (rowReader.Read())
        {
            for (var index = 0; index < rowReader.FieldCount; index++)
            {
                var value = rowReader.IsDBNull(index)
                    ? string.Empty
                    : Convert.ToString(rowReader.GetValue(index), CultureInfo.InvariantCulture) ?? string.Empty;
                AppendInventorySignatureField(hash, value);
            }
        }

        return new(
            total,
            available,
            missing,
            covered,
            outside,
            visibleOutside,
            hiddenOutside,
            Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant());
    }

    private static void EnsureTruthCoversCurrentInventory(MediaInventorySnapshot inventory, long truthRunId)
    {
        if (inventory.AvailableFiles <= 0)
            throw new InvalidOperationException("The current inventory contains no available physical media files.");
        if (inventory.AvailableOutsideTruthFiles == 0) return;

        var hiddenAdvice = inventory.HiddenAvailableOutsideTruthFiles == 0
            ? string.Empty
            : $" {inventory.HiddenAvailableOutsideTruthFiles:N0} omitted file(s) belong to hidden legacy rows and require archive reconciliation review rather than automatic movement.";
        throw new InvalidOperationException(
            $"Media consolidation stopped safely: archive reconciliation {truthRunId:N0} accounts for " +
            $"{inventory.TruthCoveredAvailableFiles:N0} of {inventory.AvailableFiles:N0} currently available physical files. " +
            $"{inventory.AvailableOutsideTruthFiles:N0} file(s) were scanned or changed after that snapshot " +
            $"({inventory.VisibleAvailableOutsideTruthFiles:N0} active, {inventory.HiddenAvailableOutsideTruthFiles:N0} hidden). " +
            "Run a fresh archive reconciliation, let scanning finish, then prepare a new consolidation plan." + hiddenAdvice);
    }

    private static void AppendInventorySignatureField(IncrementalHash hash, string value)
    {
        var prefix = Encoding.UTF8.GetBytes(value.Length.ToString(CultureInfo.InvariantCulture) + ":");
        hash.AppendData(prefix);
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData(new byte[] { (byte)'\n' });
    }

    private IReadOnlyList<string> RegisteredRoots()
    {
        var roots = new List<string>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT path FROM library_folders WHERE enabled=1";
        using var reader = command.ExecuteReader();
        while (reader.Read()) roots.Add(Path.GetFullPath(reader.GetString(0)));
        return roots;
    }

    private void ValidateOutsideRegisteredRoots(
        string managedRoot,
        string quarantineRoot,
        bool allowExactManagedRoot = false)
    {
        foreach (var root in RegisteredRoots())
        {
            var exactManagedRoot = PathsEqual(managedRoot, root);
            if ((!allowExactManagedRoot || !exactManagedRoot) &&
                (IsWithin(managedRoot, root) || IsWithin(root, managedRoot)))
                throw new InvalidOperationException("The managed destination must be a new folder outside every currently registered Library root.");
            if (IsWithin(quarantineRoot, root) || IsWithin(root, quarantineRoot))
                throw new InvalidOperationException("The quarantine must be outside every registered Library root so rejected files cannot be indexed again.");
        }
    }

    private static Variant BuildVariant(string recordingKey, Candidate[] candidates)
    {
        var internallySafe = candidates
            .GroupBy(candidate => Math.Max(1, candidate.ProposedPart))
            .All(segment => segment.Select(candidate => candidate.FullSha256)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1);
        var duration = candidates.Max(candidate => candidate.RecordingDurationMs);
        if (duration <= 0)
            duration = candidates.GroupBy(candidate => Math.Max(1, candidate.ProposedPart))
                .Sum(segment => segment.Max(candidate => candidate.DurationMs));
        var segmentBytes = candidates.GroupBy(candidate => Math.Max(1, candidate.ProposedPart))
            .Sum(segment => segment.Max(candidate => candidate.SourceBytes));
        var bitrate = EstimateBitrate(segmentBytes, duration);
        var identity = string.Join("|", candidates.Select(candidate => candidate.FullSha256)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value, StringComparer.Ordinal));
        return new(
            recordingKey,
            candidates,
            duration,
            Math.Max(0, (duration + 500) / 1000),
            bitrate,
            candidates.Max(candidate => candidate.PreferredScore),
            identity,
            internallySafe);
    }

    private static string BuildManagedPath(string root, Candidate candidate, int recordingFileCount)
    {
        return ManagedArchivePathBuilder.Build(
            root,
            candidate.ShowName,
            candidate.AirDate!.Value,
            candidate.BroadcastSlot,
            candidate.Title,
            candidate.ProposedPart,
            candidate.ProposedTotalParts,
            recordingFileCount,
            Path.GetExtension(candidate.SourcePath));
    }

    private static string BuildQuarantinePath(string root, string planId, Candidate candidate, string disposition)
    {
        var category = disposition == MediaConsolidationDisposition.ManagedCopy
            ? "Selected Originals"
            : disposition == MediaConsolidationDisposition.RejectedDuplicate ? "Exact Duplicates" : "Alternate Recordings";
        var show = ManagedArchivePathBuilder.SafeComponent(candidate.ShowName, "Unknown Show", 60);
        var canonical = ManagedArchivePathBuilder.SafeComponent(candidate.CanonicalKey, $"broadcast-{candidate.EpisodeId}", 100);
        var original = ManagedArchivePathBuilder.SafeComponent(Path.GetFileName(candidate.SourcePath), $"media-{candidate.MediaFileId}", 120);
        return Path.Combine(root, "RadioVault-Consolidation", planId, category, show, canonical,
            $"{candidate.MediaFileId}-{original}");
    }

    private static void EnsureUniqueTargets(IReadOnlyList<MediaConsolidationPlanItem> items)
    {
        var targets = new HashSet<string>(PathComparer);
        foreach (var item in items)
        {
            if (!targets.Add(Path.GetFullPath(item.QuarantinePath)))
                throw new InvalidOperationException("The consolidation plan contains a duplicate quarantine destination.");
            if (item.IsManagedCopy && !targets.Add(Path.GetFullPath(item.ManagedPath)))
                throw new InvalidOperationException("The consolidation plan contains a duplicate managed destination.");
        }
    }

    private static void EnsureUniqueSources(IReadOnlyList<MediaConsolidationPlanItem> items)
    {
        var sources = new HashSet<string>(PathComparer);
        foreach (var item in items)
            if (!sources.Add(Path.GetFullPath(item.SourcePath)))
                throw new InvalidOperationException("The consolidation plan contains the same physical source path more than once.");
    }

    private static void EnsureVerifiedManagedCopy(
        MediaConsolidationPlanItem item,
        string planId,
        CancellationToken cancellationToken)
    {
        ValidateNoDestinationLinks(item.ManagedPath);
        Directory.CreateDirectory(Path.GetDirectoryName(item.ManagedPath)!);
        if (File.Exists(item.ManagedPath))
        {
            EnsureIdentity(item.ManagedPath, item.SourceBytes, item.FullSha256, cancellationToken);
            return;
        }

        var source = File.Exists(item.SourcePath) ? item.SourcePath : item.QuarantinePath;
        EnsureIdentity(source, item.SourceBytes, item.FullSha256, cancellationToken);
        var partial = item.ManagedPath + $".{planId}.partial";
        if (File.Exists(partial))
        {
            var partialInfo = new FileInfo(partial);
            if (partialInfo.Length != item.SourceBytes ||
                !ComputeSha256(partial, cancellationToken).Equals(item.FullSha256, StringComparison.OrdinalIgnoreCase))
                File.Delete(partial); // Only an app-created partial file is ever deleted.
        }
        if (!File.Exists(partial)) CopyAndFlush(source, partial, cancellationToken);
        EnsureIdentity(partial, item.SourceBytes, item.FullSha256, cancellationToken);
        File.Move(partial, item.ManagedPath);
        EnsureIdentity(item.ManagedPath, item.SourceBytes, item.FullSha256, cancellationToken);
    }

    private static void EnsureOriginalInQuarantine(
        MediaConsolidationPlanItem item,
        CancellationToken cancellationToken)
    {
        ValidateNoDestinationLinks(item.QuarantinePath);
        Directory.CreateDirectory(Path.GetDirectoryName(item.QuarantinePath)!);
        if (File.Exists(item.QuarantinePath))
        {
            EnsureIdentity(item.QuarantinePath, item.SourceBytes, item.FullSha256, cancellationToken);
            if (File.Exists(item.SourcePath))
                throw new InvalidOperationException($"Both the source and quarantine destination exist for {Path.GetFileName(item.SourcePath)}. Nothing was overwritten.");
            return;
        }
        EnsureIdentity(item.SourcePath, item.SourceBytes, item.FullSha256, cancellationToken);
        File.Move(item.SourcePath, item.QuarantinePath);
        EnsureIdentity(item.QuarantinePath, item.SourceBytes, item.FullSha256, cancellationToken);
    }

    private int UpdateDatabase(MediaConsolidationPlan plan)
    {
        using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        var updated = 0;
        foreach (var item in plan.Items)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE media_files
                   SET path=$path,
                       original_filename=$filename,
                       is_missing=$missing,
                       storage_state=$storage,
                       is_preferred=$preferred,
                       full_hash=$hash,
                       full_hashed_at=$now,
                       last_seen_at=$now
                 WHERE id=$id AND file_size=$bytes
                """;
            var path = item.IsManagedCopy ? item.ManagedPath : item.QuarantinePath;
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$filename", item.IsManagedCopy
                ? Path.GetFileName(item.ManagedPath)
                : Path.GetFileName(item.SourcePath));
            command.Parameters.AddWithValue("$missing", item.IsManagedCopy ? 0 : 1);
            command.Parameters.AddWithValue("$storage", item.IsManagedCopy ? "AvailableOffline" : "Quarantined");
            command.Parameters.AddWithValue("$preferred", item.IsManagedCopy ? 1 : 0);
            command.Parameters.AddWithValue("$hash", item.FullSha256);
            command.Parameters.AddWithValue("$now", now);
            command.Parameters.AddWithValue("$id", item.MediaFileId);
            command.Parameters.AddWithValue("$bytes", item.SourceBytes);
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException($"Media row {item.MediaFileId} changed after rehearsal. The database transaction was rolled back.");
            updated++;

            using var rssItem = connection.CreateCommand();
            rssItem.Transaction = transaction;
            rssItem.CommandText = """
                UPDATE rss_feed_items
                   SET file_path=$path,file_name=$filename
                 WHERE file_path=$old_path;
                """;
            rssItem.Parameters.AddWithValue("$old_path", item.SourcePath);
            rssItem.Parameters.AddWithValue("$path", path);
            rssItem.Parameters.AddWithValue("$filename", Path.GetFileName(path));
            rssItem.ExecuteNonQuery();
        }

        long managedFolderId;
        using (var clearManaged = connection.CreateCommand())
        {
            clearManaged.Transaction = transaction;
            clearManaged.CommandText = "UPDATE library_folders SET is_managed_archive=0 WHERE is_managed_archive<>0;";
            clearManaged.ExecuteNonQuery();
        }
        using (var findFolder = connection.CreateCommand())
        {
            findFolder.Transaction = transaction;
            findFolder.CommandText = "SELECT id FROM library_folders WHERE lower(path)=lower($path) LIMIT 1;";
            findFolder.Parameters.AddWithValue("$path", plan.ManagedRoot);
            var existing = findFolder.ExecuteScalar();
            if (existing is not null && existing != DBNull.Value)
            {
                managedFolderId = Convert.ToInt64(existing);
                using var updateFolder = connection.CreateCommand();
                updateFolder.Transaction = transaction;
                updateFolder.CommandText = """
                    UPDATE library_folders
                       SET path=$path,assigned_collection_id=NULL,recursive=1,enabled=1,is_managed_archive=1
                     WHERE id=$id;
                    """;
                updateFolder.Parameters.AddWithValue("$id", managedFolderId);
                updateFolder.Parameters.AddWithValue("$path", plan.ManagedRoot);
                updateFolder.ExecuteNonQuery();
            }
            else
            {
                using var insertFolder = connection.CreateCommand();
                insertFolder.Transaction = transaction;
                insertFolder.CommandText = """
                    INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled,is_managed_archive)
                    VALUES($path,NULL,1,1,1)
                    RETURNING id;
                    """;
                insertFolder.Parameters.AddWithValue("$path", plan.ManagedRoot);
                managedFolderId = Convert.ToInt64(insertFolder.ExecuteScalar());
            }
        }

        using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandText = """
                INSERT INTO managed_archive_state(id,library_folder_id,managed_root,quarantine_root,consolidated_at)
                VALUES(1,$folder,$managed,$quarantine,$now)
                ON CONFLICT(id) DO UPDATE SET
                    library_folder_id=excluded.library_folder_id,
                    managed_root=excluded.managed_root,
                    quarantine_root=excluded.quarantine_root,
                    consolidated_at=excluded.consolidated_at;
                """;
            state.Parameters.AddWithValue("$folder", managedFolderId);
            state.Parameters.AddWithValue("$managed", plan.ManagedRoot);
            state.Parameters.AddWithValue("$quarantine", plan.QuarantineRoot);
            state.Parameters.AddWithValue("$now", now);
            state.ExecuteNonQuery();
        }

        using (var rssPaths = connection.CreateCommand())
        {
            rssPaths.Transaction = transaction;
            rssPaths.CommandText = """
                UPDATE rss_feed_items AS i
                   SET file_path=(
                           SELECT mf.path FROM media_files mf
                            WHERE mf.full_hash=i.content_hash COLLATE NOCASE
                              AND mf.is_missing=0 AND mf.is_preferred=1
                            ORDER BY mf.id LIMIT 1),
                       file_name=(
                           SELECT mf.original_filename FROM media_files mf
                            WHERE mf.full_hash=i.content_hash COLLATE NOCASE
                              AND mf.is_missing=0 AND mf.is_preferred=1
                            ORDER BY mf.id LIMIT 1)
                 WHERE trim(COALESCE(i.content_hash,''))<>''
                   AND EXISTS(
                           SELECT 1 FROM media_files mf
                            WHERE mf.full_hash=i.content_hash COLLATE NOCASE
                              AND mf.is_missing=0 AND mf.is_preferred=1);
                """;
            rssPaths.ExecuteNonQuery();
        }

        using (var rssFeeds = connection.CreateCommand())
        {
            rssFeeds.Transaction = transaction;
            rssFeeds.CommandText = """
                UPDATE rss_feed_subscriptions
                   SET collection_id=COALESCE(
                           collection_id,
                           (SELECT assigned_collection_id FROM library_folders
                             WHERE id=rss_feed_subscriptions.library_folder_id)),
                       library_folder_id=$managed,updated_at=$now;
                """;
            rssFeeds.Parameters.AddWithValue("$managed", managedFolderId);
            rssFeeds.Parameters.AddWithValue("$now", now);
            rssFeeds.ExecuteNonQuery();
        }

        using (var check = connection.CreateCommand())
        {
            check.Transaction = transaction;
            check.CommandText = "PRAGMA foreign_key_check";
            using var reader = check.ExecuteReader();
            if (reader.Read()) throw new InvalidOperationException("The consolidated database failed its foreign-key check.");
        }
        transaction.Commit();
        return updated;
    }

    private void CreateDatabaseBackup(string backupPath, CancellationToken cancellationToken)
    {
        if (File.Exists(backupPath))
        {
            if (!string.Equals(BackupRestoreRehearsalService.InspectQuickCheck(backupPath), "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The retained pre-consolidation database backup is not valid.");
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        var partial = backupPath + ".partial";
        if (File.Exists(partial)) File.Delete(partial); // Only an app-created partial backup is deleted.
        cancellationToken.ThrowIfCancellationRequested();
        var destinationBuilder = new SqliteConnectionStringBuilder
        {
            DataSource = partial,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        };
        using (var source = _database.OpenConnection())
        using (var destination = new SqliteConnection(destinationBuilder.ToString()))
        {
            destination.Open();
            source.BackupDatabase(destination);
        }
        if (!string.Equals(BackupRestoreRehearsalService.InspectQuickCheck(partial), "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The pre-consolidation database backup failed verification.");
        File.Move(partial, backupPath);
    }

    private static void CopyAndFlush(string source, string destination, CancellationToken cancellationToken)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            output.Write(buffer, 0, read);
        }
        output.Flush(flushToDisk: true);
    }

    private static void EnsureIdentity(string path, long expectedBytes, string expectedHash, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("A planned media file is unavailable.", path);
        if (new FileInfo(path).Length != expectedBytes ||
            !ComputeSha256(path, cancellationToken).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The file identity does not match the rehearsed plan: {path}");
    }

    private static string ComputeSha256(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string ComputePlanSignature(MediaConsolidationPlan plan)
    {
        var text = new StringBuilder()
            .AppendLine(plan.PlanId)
            .AppendLine(Path.GetFullPath(plan.ManagedRoot))
            .AppendLine(Path.GetFullPath(plan.QuarantineRoot))
            .AppendLine(plan.LibraryTruthRunId.ToString(CultureInfo.InvariantCulture))
            .AppendLine(plan.EligibleBroadcasts.ToString(CultureInfo.InvariantCulture))
            .AppendLine(plan.HeldBroadcasts.ToString(CultureInfo.InvariantCulture))
            .AppendLine(plan.InventoryMediaRecords.ToString(CultureInfo.InvariantCulture))
            .AppendLine(plan.InventoryAvailableFiles.ToString(CultureInfo.InvariantCulture))
            .AppendLine(plan.InventoryMissingFiles.ToString(CultureInfo.InvariantCulture))
            .AppendLine(plan.HeldSourceFiles.ToString(CultureInfo.InvariantCulture))
            .AppendLine(plan.InventorySignature);
        foreach (var item in plan.Items.OrderBy(value => value.ItemId, StringComparer.Ordinal))
            text.Append(item.ItemId).Append('|').Append(item.SourcePath).Append('|').Append(item.SourceBytes)
                .Append('|').Append(item.FullSha256).Append('|').Append(item.Disposition).Append('|')
                .Append(item.ManagedPath).Append('|').AppendLine(item.QuarantinePath);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();
    }

    private static void ValidatePlanSignature(MediaConsolidationPlan plan)
    {
        var actual = ComputePlanSignature(plan with { PlanSignature = string.Empty });
        if (!actual.Equals(plan.PlanSignature, StringComparison.Ordinal))
            throw new InvalidDataException("The consolidation plan changed after it was prepared.");
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, JsonOptions));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary); // Only an app-created manifest temporary is deleted.
        }
    }

    private static ConsolidationJournal? ReadJournal(string path)
    {
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<ConsolidationJournal>(File.ReadAllText(path)); }
        catch (JsonException exception) { throw new InvalidDataException("The consolidation recovery journal is damaged.", exception); }
    }

    private static void ValidateJournal(ConsolidationJournal journal, MediaConsolidationPlan plan)
    {
        if (!journal.PlanId.Equals(plan.PlanId, StringComparison.Ordinal) ||
            !journal.PlanSignature.Equals(plan.PlanSignature, StringComparison.Ordinal))
            throw new InvalidDataException("The consolidation recovery journal belongs to a different signed plan.");
    }

    private static bool IsCompletedPlan(MediaConsolidationPlan plan)
    {
        var journalPath = Path.Combine(PlanDirectory(plan), "journal.json");
        if (!File.Exists(journalPath)) return false;
        var journal = ReadJournal(journalPath);
        if (journal is null) return false;
        ValidateJournal(journal, plan);
        return journal.Status.Equals("completed", StringComparison.OrdinalIgnoreCase);
    }

    private static void WriteSafetyNotice(string planDirectory, MediaConsolidationPlan plan)
    {
        var path = Path.Combine(planDirectory, "README - REVIEW BEFORE DELETING.txt");
        if (File.Exists(path)) return;
        File.WriteAllText(path,
            "Radio Vault media consolidation quarantine\n\n" +
            $"Plan: {plan.PlanId}\n" +
            "Every original file is retained here. Radio Vault does not provide a delete command.\n" +
            "Verify the managed archive and keep a separate backup before deleting anything manually.\n" +
            "The plan.json and journal.json files record every original and destination path.\n");
    }

    private static void EnsureWritableDirectory(string root)
    {
        Directory.CreateDirectory(root);
        var probe = Path.Combine(root, $".radiovault-write-test-{Guid.NewGuid():N}");
        try
        {
            using var stream = new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.WriteByte(1);
            stream.Flush(flushToDisk: true);
        }
        finally
        {
            if (File.Exists(probe)) File.Delete(probe); // Only the app-created write probe is deleted.
        }
    }

    private static long GetAvailableBytes(string root)
    {
        try
        {
            var drive = FindStorageVolume(root);
            return drive?.AvailableFreeSpace ?? -1;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return -1;
        }
    }

    private static bool SameStorageVolume(string left, string right)
    {
        var leftVolume = FindStorageVolume(left)?.RootDirectory.FullName;
        var rightVolume = FindStorageVolume(right)?.RootDirectory.FullName;
        return !string.IsNullOrWhiteSpace(leftVolume) &&
               !string.IsNullOrWhiteSpace(rightVolume) &&
               PathsEqual(leftVolume, rightVolume);
    }

    private static DriveInfo? FindStorageVolume(string path)
    {
        var fullPath = Path.GetFullPath(path);
        return DriveInfo.GetDrives()
            .Where(drive => IsWithin(fullPath, drive.RootDirectory.FullName))
            .OrderByDescending(drive => drive.RootDirectory.FullName.Length)
            .FirstOrDefault();
    }

    private static string NormalizeRoot(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A folder is required.", parameterName);
        return Path.GetFullPath(value).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static void ValidateRootRelationship(string managedRoot, string quarantineRoot)
    {
        var managed = NormalizeRoot(managedRoot, nameof(managedRoot));
        var quarantine = NormalizeRoot(quarantineRoot, nameof(quarantineRoot));
        if (IsWithin(managed, quarantine) || IsWithin(quarantine, managed))
            throw new InvalidOperationException("The managed archive and quarantine must be separate, non-nested folders.");
    }

    private static bool IsWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(fullRoot, PathComparison) ||
               fullPath.StartsWith(fullRoot + Path.DirectorySeparatorChar, PathComparison) ||
               fullPath.StartsWith(fullRoot + Path.AltDirectorySeparatorChar, PathComparison);
    }

    private static bool PathsEqual(string left, string right)
        => Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Equals(
                Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                PathComparison);

    private static void ValidateNoDestinationLinks(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"A consolidation destination cannot be a symbolic link: {path}");
        var current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Consolidation destinations cannot pass through a symbolic link: {current.FullName}");
            current = current.Parent;
        }
    }

    private static bool IsSafeAdoptionState(string value)
        => value.Equals("Ready", StringComparison.OrdinalIgnoreCase) ||
           value.Equals("Ready with recording choice", StringComparison.OrdinalIgnoreCase);

    private static string HeldReason(Candidate candidate)
        => candidate.AirDate is null
            ? "the broadcast date is not known"
            : !IsSafeAdoptionState(candidate.AdoptionState)
                ? $"Archive reconciliation state is {candidate.AdoptionState}"
                : "the recording structure is incomplete";

    private static long EstimateBitrate(long bytes, long durationMs)
        => bytes <= 0 || durationMs <= 0 ? 0 : (long)Math.Round(bytes * 8_000d / durationMs);

    private static string BuildItemId(Candidate candidate)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{candidate.CanonicalKey}|{candidate.RecordingKey}|{candidate.ProposedPart}|{candidate.FullSha256}|{candidate.MediaFileId}")))
            .ToLowerInvariant()[..24];

    private static string PlanDirectory(MediaConsolidationPlan plan)
        => Path.Combine(plan.QuarantineRoot, "RadioVault-Consolidation", plan.PlanId);

    private static string FormatDuration(long value)
        => value <= 0 ? "unknown duration" : TimeSpan.FromMilliseconds(value).ToString(@"h\:mm\:ss");

    private static string FormatBitrate(long value)
        => value <= 0 ? "unknown bitrate" : $"{Math.Round(value / 1000d):N0} kbps";

    private static string FormatBytes(long value)
        => value >= 1024L * 1024 * 1024 ? $"{value / (1024d * 1024 * 1024):0.0} GB"
            : value >= 1024L * 1024 ? $"{value / (1024d * 1024):0.0} MB"
            : $"{value / 1024d:0.0} KB";

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private sealed record Candidate(
        long MediaFileId,
        long EpisodeId,
        string SourcePath,
        long SourceBytes,
        long DurationMs,
        string StoredFullSha256,
        string FullSha256,
        string CanonicalKey,
        string RecordingKey,
        string ShowName,
        DateOnly? AirDate,
        string BroadcastSlot,
        int ProposedPart,
        int? ProposedTotalParts,
        string Title,
        string AdoptionState,
        long RecordingDurationMs,
        int PreferredScore,
        long EstimatedBitrate);

    private sealed record Variant(
        string RecordingKey,
        Candidate[] Candidates,
        long DurationMs,
        long DurationSecondBucket,
        long EstimatedBitrate,
        int PreferredScore,
        string ContentIdentity,
        bool IsInternallySafe);

    private sealed record MediaInventorySnapshot(
        int TotalMediaRecords,
        int AvailableFiles,
        int MissingFiles,
        int TruthCoveredAvailableFiles,
        int AvailableOutsideTruthFiles,
        int VisibleAvailableOutsideTruthFiles,
        int HiddenAvailableOutsideTruthFiles,
        string Signature);

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private sealed class ConsolidationJournal
    {
        public string PlanId { get; set; } = string.Empty;
        public string PlanSignature { get; set; } = string.Empty;
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string Status { get; set; } = string.Empty;
        public HashSet<string> ManagedItemIds { get; set; } = new(StringComparer.Ordinal);
        public HashSet<string> QuarantinedItemIds { get; set; } = new(StringComparer.Ordinal);
        public int DatabaseRowsUpdated { get; set; }
    }
}
