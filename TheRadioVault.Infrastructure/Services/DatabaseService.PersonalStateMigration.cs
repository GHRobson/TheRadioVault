using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Services;
using TheRadioVault.Models;

namespace TheRadioVault.Services;

public sealed partial class DatabaseService
{
    private const int PersonalStateSchemaVersion = 1;
    private const int PersonalStateMaximumRecords = 100_000;
    private const long PersonalStateMaximumJsonBytes = 32L * 1024 * 1024;
    private static readonly JsonSerializerOptions PersonalStateJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public PersonalStatePackManifest ExportPersonalStatePack(string destinationPath, string appVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        var episodes = GetEpisodes();
        var identityByEpisode = episodes.GroupBy(episode => episode.Id).ToDictionary(group => group.Key, group => CreatePortableIdentity(group.First()));
        var pack = new PersonalStatePack();

        foreach (var episode in episodes)
        {
            var state = GetPlaybackState(episode.Id);
            if (state.PositionMs <= 0 && !state.Completed && !episode.Favourite &&
                state.PlayCount <= 0 && state.CompletionCount <= 0 && !state.LastPlayedAt.HasValue)
                continue;

            pack.Broadcasts.Add(new PersonalStateBroadcastRecord
            {
                Identity = identityByEpisode[episode.Id],
                PositionMs = Math.Max(0, state.PositionMs),
                DurationMs = Math.Max(episode.DurationMs, state.DurationMs),
                Completed = state.Completed || string.Equals(episode.Status, "Completed", StringComparison.OrdinalIgnoreCase),
                Favourite = episode.Favourite,
                PlaybackSpeed = state.PlaybackSpeed > 0 ? state.PlaybackSpeed : 1d,
                FirstPlayedAtUtc = ToUtc(state.FirstPlayedAt),
                LastPlayedAtUtc = ToUtc(state.LastPlayedAt),
                PlayCount = Math.Max(0, state.PlayCount),
                CompletionCount = Math.Max(0, state.CompletionCount)
            });
        }

        foreach (var moment in GetMoments().OrderBy(moment => moment.CreatedAt).ThenBy(moment => moment.Id))
        {
            if (!identityByEpisode.TryGetValue(moment.EpisodeId, out var identity))
            {
                var resolution = ResolveCanonicalEpisode(moment.EpisodeId);
                if (resolution is null || !identityByEpisode.TryGetValue(resolution.RepresentativeEpisodeId, out identity))
                    continue;
            }

            pack.Moments.Add(new PersonalStateMomentRecord
            {
                Identity = CloneIdentity(identity),
                PositionMs = Math.Max(0, moment.PositionMs),
                Title = moment.Title ?? string.Empty,
                Notes = moment.Notes ?? string.Empty,
                CreatedAtUtc = ToUtc(moment.CreatedAt) ?? DateTimeOffset.UtcNow
            });
        }

        foreach (var queueItem in GetQueue().OrderBy(item => item.Position).ThenBy(item => item.QueueId))
        {
            if (!identityByEpisode.TryGetValue(queueItem.EpisodeId, out var identity))
            {
                var resolution = ResolveCanonicalEpisode(queueItem.EpisodeId);
                if (resolution is null || !identityByEpisode.TryGetValue(resolution.RepresentativeEpisodeId, out identity))
                    continue;
            }

            pack.Queue.Add(new PersonalStateQueueRecord
            {
                Identity = CloneIdentity(identity),
                Position = pack.Queue.Count
            });
        }

        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(pack, PersonalStateJsonOptions);
        var manifest = new PersonalStatePackManifest
        {
            SchemaVersion = PersonalStateSchemaVersion,
            PackageType = "radio-vault-personal-state",
            AppVersion = appVersion,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            SourceMachineName = Environment.MachineName,
            StateSha256 = Convert.ToHexString(SHA256.HashData(stateBytes)).ToLowerInvariant(),
            BroadcastStateCount = pack.Broadcasts.Count,
            MomentCount = pack.Moments.Count,
            QueueCount = pack.Queue.Count
        };
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, PersonalStateJsonOptions);

        var fullPath = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath) ?? AppPaths.DataDirectory);
        var temporaryPath = fullPath + ".writing-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false))
            {
                WriteZipEntry(archive, "manifest.json", manifestBytes);
                WriteZipEntry(archive, "state.json", stateBytes);
            }
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        return manifest;
    }

    public PersonalStateImportPreview PreviewPersonalStateImport(string sourcePath)
    {
        var loaded = ReadPersonalStatePack(sourcePath);
        return BuildPersonalStatePreview(sourcePath, loaded.Manifest, loaded.Pack);
    }

    public PersonalStateImportResult ImportPersonalStatePack(string sourcePath)
    {
        var loaded = ReadPersonalStatePack(sourcePath);
        var preview = BuildPersonalStatePreview(sourcePath, loaded.Manifest, loaded.Pack);
        if (!preview.CanApply)
            throw new InvalidOperationException("The migration pack has no safe changes that can be applied to this library.");

        var episodes = GetEpisodes();
        var lookup = BuildDestinationLookup(episodes);
        var playbackPlans = new List<PlaybackMigrationPlan>();
        var favouritePlans = new List<FavouriteMigrationPlan>();
        var completionAdditions = 0;

        foreach (var source in loaded.Pack.Broadcasts)
        {
            var destination = MatchDestination(source.Identity, lookup);
            if (destination is null) continue;
            var existing = GetPlaybackState(destination.Id);
            var merge = BuildPlaybackMerge(source, existing, destination.DurationMs);
            if (merge.HasChanges)
            {
                playbackPlans.Add(new PlaybackMigrationPlan(
                    destination,
                    ExpandCanonicalStateEpisodeIds(destination.Id),
                    merge));
                if (!existing.Completed && merge.Completed) completionAdditions++;
            }
            if (source.Favourite && !destination.Favourite)
                favouritePlans.Add(new FavouriteMigrationPlan(destination, ExpandCanonicalStateEpisodeIds(destination.Id)));
        }

        var existingMoments = GetMoments();
        var momentPlans = new List<MomentMigrationPlan>();
        foreach (var source in loaded.Pack.Moments)
        {
            var destination = MatchDestination(source.Identity, lookup);
            if (destination is null) continue;
            var duplicate = existingMoments.Any(moment =>
                moment.EpisodeId == destination.Id &&
                AreEquivalentMoment(
                    moment.PositionMs,
                    moment.Title,
                    moment.Notes,
                    source.PositionMs,
                    source.Title,
                    source.Notes)) ||
                momentPlans.Any(plan =>
                    plan.EpisodeId == destination.Id &&
                    AreEquivalentMoment(
                        plan.Source.PositionMs,
                        plan.Source.Title,
                        plan.Source.Notes,
                        source.PositionMs,
                        source.Title,
                        source.Notes));
            if (!duplicate) momentPlans.Add(new MomentMigrationPlan(destination.Id, source));
        }

        var episodeById = episodes.GroupBy(episode => episode.Id).ToDictionary(group => group.Key, group => group.First());
        var queuedCanonicalKeys = GetQueue()
            .Select(item => episodeById.TryGetValue(item.EpisodeId, out var episode) ? DestinationKey(episode) : null)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queuePlans = new List<EpisodeListItem>();
        foreach (var source in loaded.Pack.Queue.OrderBy(item => item.Position))
        {
            var destination = MatchDestination(source.Identity, lookup);
            if (destination is null) continue;
            var key = DestinationKey(destination);
            if (!queuedCanonicalKeys.Add(key)) continue;
            queuePlans.Add(destination);
        }

        var backupPath = CreatePersonalStateMigrationBackup();
        var importedAt = DateTimeOffset.UtcNow;
        using (var connection = OpenConnection())
        using (var transaction = connection.BeginTransaction())
        {
            foreach (var plan in playbackPlans)
                ApplyPlaybackMigration(connection, transaction, plan, importedAt);

            foreach (var favouritePlan in favouritePlans.GroupBy(item => item.Destination.Id).Select(group => group.First()))
            {
                foreach (var episodeId in favouritePlan.StateEpisodeIds)
                {
                    using var favourite = connection.CreateCommand();
                    favourite.Transaction = transaction;
                    favourite.CommandText = "UPDATE episodes SET favourite=1,updated_at=$now WHERE id=$id";
                    favourite.Parameters.AddWithValue("$now", importedAt.UtcDateTime.ToString("O"));
                    favourite.Parameters.AddWithValue("$id", episodeId);
                    favourite.ExecuteNonQuery();
                }
            }

            foreach (var plan in momentPlans)
                AddMomentIdempotent(
                    connection,
                    transaction,
                    plan.EpisodeId,
                    plan.Source.PositionMs,
                    plan.Source.Title,
                    plan.Source.Notes,
                    plan.Source.CreatedAtUtc.UtcDateTime.ToString("O"));

            var nextQueuePosition = ReadNextQueuePosition(connection, transaction);
            foreach (var destination in queuePlans)
            {
                using var queue = connection.CreateCommand();
                queue.Transaction = transaction;
                queue.CommandText = "INSERT INTO playback_queue(episode_id,queue_position,added_at) VALUES($episode,$position,$added)";
                queue.Parameters.AddWithValue("$episode", destination.Id);
                queue.Parameters.AddWithValue("$position", nextQueuePosition++);
                queue.Parameters.AddWithValue("$added", importedAt.UtcDateTime.ToString("O"));
                queue.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        var result = new PersonalStateImportResult
        {
            BackupPath = backupPath,
            PlaybackRecordsUpdated = playbackPlans.Count,
            FavouritesAdded = favouritePlans.Select(item => item.Destination.Id).Distinct().Count(),
            CompletionsAdded = completionAdditions,
            MomentsAdded = momentPlans.Count,
            QueueItemsAdded = queuePlans.Count,
            UnmatchedItems = preview.UnmatchedItems.Count,
            ProtectedItems = preview.ProtectedItems.Count
        };
        try
        {
            result.ReportPath = WritePersonalStateImportReport(sourcePath, loaded.Manifest, preview, result, importedAt);
        }
        catch (Exception ex)
        {
            DiagnosticLog.Write("Personal state migration", $"Import succeeded but the report could not be written: {ex.Message}");
            result.ReportPath = "Import completed; the report could not be written.";
        }
        return result;
    }

    private PersonalStateImportPreview BuildPersonalStatePreview(
        string sourcePath,
        PersonalStatePackManifest manifest,
        PersonalStatePack pack)
    {
        var episodes = GetEpisodes();
        var lookup = BuildDestinationLookup(episodes);
        var preview = new PersonalStateImportPreview
        {
            SourcePath = sourcePath,
            Manifest = manifest
        };
        var unmatched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in pack.Broadcasts)
        {
            var destination = MatchDestination(source.Identity, lookup);
            if (destination is null)
            {
                preview.UnmatchedBroadcasts++;
                unmatched.Add(source.Identity.Display);
                continue;
            }

            preview.MatchedBroadcasts++;
            var existing = GetPlaybackState(destination.Id);
            var merge = BuildPlaybackMerge(source, existing, destination.DurationMs);
            if (merge.HasChanges) preview.ProgressUpdates++;
            if (merge.PreservedNewerDestination)
            {
                preview.PreservedNewerDesktopProgress++;
                preview.ProtectedItems.Add($"{source.Identity.Display}: newer or completed server progress will be preserved.");
            }
            if (source.Favourite && !destination.Favourite) preview.FavouriteAdditions++;
            if (source.Completed && !existing.Completed) preview.CompletionAdditions++;
        }

        var existingMoments = GetMoments();
        var previewMomentPlans = new List<MomentMigrationPlan>();
        foreach (var source in pack.Moments)
        {
            var destination = MatchDestination(source.Identity, lookup);
            if (destination is null)
            {
                unmatched.Add(source.Identity.Display);
                continue;
            }
            var duplicate = existingMoments.Any(moment =>
                moment.EpisodeId == destination.Id &&
                AreEquivalentMoment(
                    moment.PositionMs,
                    moment.Title,
                    moment.Notes,
                    source.PositionMs,
                    source.Title,
                    source.Notes)) ||
                previewMomentPlans.Any(plan =>
                    plan.EpisodeId == destination.Id &&
                    AreEquivalentMoment(
                        plan.Source.PositionMs,
                        plan.Source.Title,
                        plan.Source.Notes,
                        source.PositionMs,
                        source.Title,
                        source.Notes));
            if (duplicate) preview.DuplicateMoments++;
            else
            {
                preview.MomentAdditions++;
                previewMomentPlans.Add(new MomentMigrationPlan(destination.Id, source));
            }
        }

        var episodeById = episodes.GroupBy(episode => episode.Id).ToDictionary(group => group.Key, group => group.First());
        var queuedCanonicalKeys = GetQueue()
            .Select(item => episodeById.TryGetValue(item.EpisodeId, out var episode) ? DestinationKey(episode) : null)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Select(key => key!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var source in pack.Queue.OrderBy(item => item.Position))
        {
            var destination = MatchDestination(source.Identity, lookup);
            if (destination is null)
            {
                unmatched.Add(source.Identity.Display);
                continue;
            }
            if (queuedCanonicalKeys.Add(DestinationKey(destination))) preview.QueueAdditions++;
            else preview.DuplicateQueueItems++;
        }

        preview.UnmatchedItems = unmatched.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).Take(500).ToList();
        return preview;
    }

    private static PlaybackMergeDecision BuildPlaybackMerge(
        PersonalStateBroadcastRecord source,
        PlaybackState destination,
        long destinationDurationMs)
    {
        var destinationLast = ToUtc(destination.LastPlayedAt);
        var sourceLast = source.LastPlayedAtUtc?.ToUniversalTime();
        var sourceIsNewer = sourceLast.HasValue && (!destinationLast.HasValue || sourceLast.Value > destinationLast.Value.AddSeconds(1));
        var destinationIsNewer = destinationLast.HasValue && sourceLast.HasValue && destinationLast.Value > sourceLast.Value.AddSeconds(1);
        var completed = destination.Completed || source.Completed;
        long position;
        var preservedNewerDestination = false;

        if (destination.Completed && !source.Completed)
        {
            position = destination.PositionMs;
            preservedNewerDestination = source.PositionMs != destination.PositionMs;
        }
        else if (source.Completed && !destination.Completed)
        {
            position = source.DurationMs > 0 ? source.DurationMs : Math.Max(source.PositionMs, destination.PositionMs);
        }
        else if (sourceIsNewer)
        {
            position = Math.Max(0, source.PositionMs);
        }
        else if (destinationIsNewer)
        {
            position = destination.PositionMs;
            preservedNewerDestination = source.PositionMs != destination.PositionMs;
        }
        else
        {
            position = Math.Max(destination.PositionMs, source.PositionMs);
        }

        var duration = Math.Max(Math.Max(0, destination.DurationMs), Math.Max(destinationDurationMs, source.DurationMs));
        if (duration > 0) position = Math.Clamp(position, 0, duration);
        var firstPlayed = Min(ToUtc(destination.FirstPlayedAt), source.FirstPlayedAtUtc?.ToUniversalTime());
        var lastPlayed = Max(destinationLast, sourceLast);
        var playCount = Math.Max(destination.PlayCount, source.PlayCount);
        var completionCount = Math.Max(destination.CompletionCount, source.CompletionCount);
        var speed = sourceIsNewer && source.PlaybackSpeed > 0
            ? source.PlaybackSpeed
            : destination.PlaybackSpeed > 0 ? destination.PlaybackSpeed : source.PlaybackSpeed > 0 ? source.PlaybackSpeed : 1d;

        var hasChanges = position != destination.PositionMs ||
                         duration > destination.DurationMs ||
                         completed != destination.Completed ||
                         playCount > destination.PlayCount ||
                         completionCount > destination.CompletionCount ||
                         firstPlayed != ToUtc(destination.FirstPlayedAt) ||
                         lastPlayed != destinationLast ||
                         Math.Abs(speed - destination.PlaybackSpeed) > 0.001;

        return new PlaybackMergeDecision(
            position,
            duration,
            completed,
            speed,
            firstPlayed,
            lastPlayed,
            playCount,
            completionCount,
            hasChanges,
            preservedNewerDestination);
    }

    private static void ApplyPlaybackMigration(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PlaybackMigrationPlan plan,
        DateTimeOffset importedAt)
    {
        foreach (var episodeId in plan.StateEpisodeIds)
        {
            using var playback = connection.CreateCommand();
            playback.Transaction = transaction;
            playback.CommandText = """
                INSERT INTO playback_state(
                    episode_id,position_ms,completed,last_played_at,play_count,duration_ms,playback_speed,
                    completed_at,first_played_at,completion_count)
                VALUES($id,$position,$completed,$last,$playCount,$duration,$speed,$completedAt,$first,$completionCount)
                ON CONFLICT(episode_id) DO UPDATE SET
                    position_ms=$position,
                    completed=$completed,
                    last_played_at=$last,
                    play_count=MAX(playback_state.play_count,$playCount),
                    duration_ms=MAX(playback_state.duration_ms,$duration),
                    playback_speed=$speed,
                    completed_at=CASE WHEN $completed=1 THEN COALESCE(playback_state.completed_at,$completedAt,$last) ELSE playback_state.completed_at END,
                    first_played_at=CASE
                        WHEN playback_state.first_played_at IS NULL THEN $first
                        WHEN $first IS NULL THEN playback_state.first_played_at
                        WHEN $first < playback_state.first_played_at THEN $first
                        ELSE playback_state.first_played_at END,
                    completion_count=MAX(playback_state.completion_count,$completionCount)
                """;
            playback.Parameters.AddWithValue("$id", episodeId);
            playback.Parameters.AddWithValue("$position", plan.Merge.PositionMs);
            playback.Parameters.AddWithValue("$completed", plan.Merge.Completed ? 1 : 0);
            playback.Parameters.AddWithValue("$last", plan.Merge.LastPlayedAtUtc?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
            playback.Parameters.AddWithValue("$playCount", plan.Merge.PlayCount);
            playback.Parameters.AddWithValue("$duration", plan.Merge.DurationMs);
            playback.Parameters.AddWithValue("$speed", plan.Merge.PlaybackSpeed);
            playback.Parameters.AddWithValue("$completedAt", plan.Merge.Completed
                ? plan.Merge.LastPlayedAtUtc?.UtcDateTime.ToString("O") ?? importedAt.UtcDateTime.ToString("O")
                : (object)DBNull.Value);
            playback.Parameters.AddWithValue("$first", plan.Merge.FirstPlayedAtUtc?.UtcDateTime.ToString("O") ?? (object)DBNull.Value);
            playback.Parameters.AddWithValue("$completionCount", plan.Merge.CompletionCount);
            playback.ExecuteNonQuery();

            using var episode = connection.CreateCommand();
            episode.Transaction = transaction;
            episode.CommandText = "UPDATE episodes SET status=$status,updated_at=$now WHERE id=$id";
            episode.Parameters.AddWithValue("$status", plan.Merge.Completed ? "Completed" : plan.Merge.PositionMs > 0 ? "In Progress" : "Unplayed");
            episode.Parameters.AddWithValue("$now", importedAt.UtcDateTime.ToString("O"));
            episode.Parameters.AddWithValue("$id", episodeId);
            episode.ExecuteNonQuery();
        }
    }

    private static string CreatePersonalStateMigrationBackup()
    {
        var backupPath = Path.Combine(
            AppPaths.BackupDirectory,
            $"RadioVault-before-personal-state-import-{DateTime.Now:yyyy-MM-dd-HHmmss-fff}.trvbackup");
        return new BackupService().CreateBackup(backupPath);
    }

    private static int ReadNextQueuePosition(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COALESCE(MAX(queue_position),-1)+1 FROM playback_queue";
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static PersonalStateBroadcastIdentity CreatePortableIdentity(EpisodeListItem episode) => new()
    {
        CanonicalKey = episode.CanonicalKey ?? string.Empty,
        CollectionName = episode.CollectionName ?? string.Empty,
        AirDate = episode.AirDate?.ToString("yyyy-MM-dd"),
        BroadcastSlot = episode.BroadcastSlot ?? string.Empty,
        BroadcastUid = episode.BroadcastUid ?? string.Empty,
        Headline = episode.Headline ?? string.Empty
    };

    private static PersonalStateBroadcastIdentity CloneIdentity(PersonalStateBroadcastIdentity identity) => new()
    {
        CanonicalKey = identity.CanonicalKey,
        CollectionName = identity.CollectionName,
        AirDate = identity.AirDate,
        BroadcastSlot = identity.BroadcastSlot,
        BroadcastUid = identity.BroadcastUid,
        Headline = identity.Headline
    };

    private static DestinationLookup BuildDestinationLookup(IReadOnlyList<EpisodeListItem> episodes)
    {
        var canonical = episodes
            .Where(episode => !string.IsNullOrWhiteSpace(episode.CanonicalKey))
            .GroupBy(episode => episode.CanonicalKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var uid = episodes
            .Where(episode => !string.IsNullOrWhiteSpace(episode.BroadcastUid))
            .GroupBy(episode => episode.BroadcastUid, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var fallback = episodes
            .GroupBy(CreateFallbackKey, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return new DestinationLookup(canonical, uid, fallback);
    }

    private static EpisodeListItem? MatchDestination(PersonalStateBroadcastIdentity source, DestinationLookup lookup)
    {
        if (!string.IsNullOrWhiteSpace(source.CanonicalKey) && lookup.Canonical.TryGetValue(source.CanonicalKey, out var canonical))
            return canonical;
        if (!string.IsNullOrWhiteSpace(source.BroadcastUid) && lookup.BroadcastUid.TryGetValue(source.BroadcastUid, out var uid))
            return uid;
        return lookup.Fallback.TryGetValue(CreateFallbackKey(source), out var fallback) ? fallback : null;
    }

    private static string CreateFallbackKey(EpisodeListItem episode) => string.Join('|',
        NormalizeIdentity(episode.CollectionName),
        episode.AirDate?.ToString("yyyy-MM-dd") ?? string.Empty,
        NormalizeIdentity(episode.BroadcastSlot));

    private static string CreateFallbackKey(PersonalStateBroadcastIdentity identity) => string.Join('|',
        NormalizeIdentity(identity.CollectionName),
        identity.AirDate?.Trim() ?? string.Empty,
        NormalizeIdentity(identity.BroadcastSlot));

    private static string DestinationKey(EpisodeListItem episode) => !string.IsNullOrWhiteSpace(episode.CanonicalKey)
        ? episode.CanonicalKey
        : CreateFallbackKey(episode);


    private static bool AreEquivalentMoment(
        long positionA,
        string? titleA,
        string? notesA,
        long positionB,
        string? titleB,
        string? notesB)
        => Math.Abs(Math.Max(0, positionA) - Math.Max(0, positionB)) <= MomentDeduplicationPolicy.PositionToleranceMs &&
           string.Equals(MomentDeduplicationPolicy.NormalizeText(titleA), MomentDeduplicationPolicy.NormalizeText(titleB), StringComparison.Ordinal) &&
           string.Equals(MomentDeduplicationPolicy.NormalizeText(notesA), MomentDeduplicationPolicy.NormalizeText(notesB), StringComparison.Ordinal);

    private static string NormalizeIdentity(string? value) => string.Join(" ", (value ?? string.Empty)
        .Trim()
        .ToLowerInvariant()
        .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static DateTimeOffset? ToUtc(DateTime? value)
    {
        if (!value.HasValue) return null;
        var dateTime = value.Value;
        if (dateTime.Kind == DateTimeKind.Unspecified) dateTime = DateTime.SpecifyKind(dateTime, DateTimeKind.Local);
        return dateTime.ToUniversalTime();
    }

    private static DateTimeOffset? Min(DateTimeOffset? left, DateTimeOffset? right)
        => !left.HasValue ? right : !right.HasValue ? left : left.Value <= right.Value ? left : right;

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset? right)
        => !left.HasValue ? right : !right.HasValue ? left : left.Value >= right.Value ? left : right;

    private static void WriteZipEntry(ZipArchive archive, string name, byte[] bytes)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(bytes, 0, bytes.Length);
    }

    private static (PersonalStatePackManifest Manifest, PersonalStatePack Pack) ReadPersonalStatePack(string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        using var stream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        var manifestEntry = archive.GetEntry("manifest.json")
                            ?? throw new InvalidDataException("The migration pack does not contain manifest.json.");
        var stateEntry = archive.GetEntry("state.json")
                         ?? throw new InvalidDataException("The migration pack does not contain state.json.");
        if (manifestEntry.Length > 1_000_000 || stateEntry.Length > PersonalStateMaximumJsonBytes)
            throw new InvalidDataException("The migration pack is larger than Radio Vault's safe import limit.");

        var manifestBytes = ReadEntryBytes(manifestEntry, 1_000_000);
        var stateBytes = ReadEntryBytes(stateEntry, PersonalStateMaximumJsonBytes);
        var manifest = JsonSerializer.Deserialize<PersonalStatePackManifest>(manifestBytes, PersonalStateJsonOptions)
                       ?? throw new InvalidDataException("The migration manifest is empty or invalid.");
        if (manifest.SchemaVersion != PersonalStateSchemaVersion ||
            !string.Equals(manifest.PackageType, "radio-vault-personal-state", StringComparison.Ordinal))
            throw new InvalidDataException($"This personal-state package uses unsupported schema {manifest.SchemaVersion}.");

        var actualHash = Convert.ToHexString(SHA256.HashData(stateBytes)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actualHash),
                Encoding.ASCII.GetBytes(manifest.StateSha256?.ToLowerInvariant() ?? string.Empty)))
            throw new InvalidDataException("The migration pack failed its integrity check and may be incomplete or edited.");

        var pack = JsonSerializer.Deserialize<PersonalStatePack>(stateBytes, PersonalStateJsonOptions)
                   ?? throw new InvalidDataException("The migration state is empty or invalid.");
        if (pack.Broadcasts is null || pack.Moments is null || pack.Queue is null ||
            pack.Broadcasts.Any(record => record is null || record.Identity is null) ||
            pack.Moments.Any(record => record is null || record.Identity is null) ||
            pack.Queue.Any(record => record is null || record.Identity is null))
            throw new InvalidDataException("The migration pack contains incomplete records.");
        var totalRecords = (long)pack.Broadcasts.Count + pack.Moments.Count + pack.Queue.Count;
        if (manifest.BroadcastStateCount != pack.Broadcasts.Count ||
            manifest.MomentCount != pack.Moments.Count ||
            manifest.QueueCount != pack.Queue.Count)
            throw new InvalidDataException("The migration manifest counts do not match its sealed state payload.");
        if (totalRecords > PersonalStateMaximumRecords)
            throw new InvalidDataException("The migration pack contains too many records to import safely.");
        return (manifest, pack);
    }

    private static byte[] ReadEntryBytes(ZipArchiveEntry entry, long maximumBytes)
    {
        using var source = entry.Open();
        using var destination = new MemoryStream();
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > maximumBytes) throw new InvalidDataException("A migration-pack entry exceeds the safe size limit.");
            destination.Write(buffer, 0, read);
        }
        return destination.ToArray();
    }

    private static string WritePersonalStateImportReport(
        string sourcePath,
        PersonalStatePackManifest manifest,
        PersonalStateImportPreview preview,
        PersonalStateImportResult result,
        DateTimeOffset importedAt)
    {
        var directory = Path.Combine(AppPaths.DataDirectory, "MigrationReports");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"PersonalStateImport-{importedAt.LocalDateTime:yyyy-MM-dd-HHmmss}.json");
        var report = new
        {
            schemaVersion = 1,
            importedAtUtc = importedAt,
            sourcePackageName = Path.GetFileName(sourcePath),
            sourceMachine = manifest.SourceMachineName,
            sourceCreatedAtUtc = manifest.CreatedAtUtc,
            sourceStateSha256 = manifest.StateSha256,
            backupPath = result.BackupPath,
            result.PlaybackRecordsUpdated,
            result.FavouritesAdded,
            result.CompletionsAdded,
            result.MomentsAdded,
            result.QueueItemsAdded,
            unmatchedItems = preview.UnmatchedItems,
            protectedItems = preview.ProtectedItems
        };
        File.WriteAllText(path, JsonSerializer.Serialize(report, PersonalStateJsonOptions), new UTF8Encoding(false));
        return path;
    }

    private sealed record DestinationLookup(
        IReadOnlyDictionary<string, EpisodeListItem> Canonical,
        IReadOnlyDictionary<string, EpisodeListItem> BroadcastUid,
        IReadOnlyDictionary<string, EpisodeListItem> Fallback);

    private sealed record PlaybackMergeDecision(
        long PositionMs,
        long DurationMs,
        bool Completed,
        double PlaybackSpeed,
        DateTimeOffset? FirstPlayedAtUtc,
        DateTimeOffset? LastPlayedAtUtc,
        int PlayCount,
        int CompletionCount,
        bool HasChanges,
        bool PreservedNewerDestination);

    private sealed record PlaybackMigrationPlan(
        EpisodeListItem Destination,
        IReadOnlyList<long> StateEpisodeIds,
        PlaybackMergeDecision Merge);

    private sealed record FavouriteMigrationPlan(EpisodeListItem Destination, IReadOnlyList<long> StateEpisodeIds);

    private sealed record MomentMigrationPlan(long EpisodeId, PersonalStateMomentRecord Source);
}
