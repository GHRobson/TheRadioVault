using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.LibraryTruth;
using TheRadioVault.Core.Services;
using TheRadioVault.Data.Database;
using TheRadioVault.Models;
using TheRadioVault.Services;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Services.Services;

namespace TheRadioVault.Desktop.Avalonia.Research;

public sealed class LocalResearchPackTransferService : IResearchPackTransferService
{
    private readonly SqliteDatabase _database;
    private readonly KnowledgePackService _packs = new();
    private PendingImport? _pending;

    public LocalResearchPackTransferService(SqliteDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public bool IsAvailable => true;
    public bool IsRemoteOwned => false;

    public async Task<ResearchPackPreviewSummary> PreviewImportAsync(
        string filePath,
        IProgress<ResearchPackTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("The selected Knowledge Database no longer exists.", filePath);
        progress?.Report(new ResearchPackTransferProgress(2, "Reading the Knowledge Database…", Phase: "Preview"));
        var pack = await Task.Run(() => _packs.Import(filePath), cancellationToken).ConfigureAwait(false);
        string hash;
        await using (var stream = File.OpenRead(filePath))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();

        var operationProgress = new Progress<ResearchPackOperationProgress>(value =>
            progress?.Report(new ResearchPackTransferProgress(
                10 + value.Percent * 0.88,
                value.Message,
                value.Current,
                value.Total,
                "Preview")));
        var preview = await Task.Run(
            () => new DatabaseService(_database).PreviewKnowledgePack(pack, filePath, operationProgress, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        _pending = new PendingImport(filePath, hash, pack);
        var wikiPages = pack.Wiki?.Pages.Count ?? 0;
        var wikiImages = pack.Wiki?.Images.Count ?? 0;
        var wikiTimeline = pack.Wiki?.TimelineEvents.Count ?? 0;
        progress?.Report(new ResearchPackTransferProgress(100, "Knowledge Database analysis complete.", preview.TotalRecords, preview.TotalRecords, "Preview"));
        return new ResearchPackPreviewSummary(
            Path.GetFileName(filePath),
            Clean(pack.Manifest.Show),
            preview.TotalRecords,
            pack.Transcripts?.Count ?? 0,
            preview.ExactMatches,
            preview.MissingRecords,
            preview.AmbiguousMatches,
            preview.AuthoritativeAudit,
            preview.Summary,
            wikiPages,
            wikiImages,
            wikiTimeline);
    }

    private async Task<ResearchPackPreviewSummary> PreviewImportLegacyAsync(
        string filePath,
        IProgress<ResearchPackTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("The selected research pack no longer exists.", filePath);
        progress?.Report(new ResearchPackTransferProgress(2, "Reading the Knowledge Database…", Phase: "Preview"));
        var pack = await Task.Run(() => _packs.Import(filePath), cancellationToken).ConfigureAwait(false);
        string hash;
        await using (var stream = File.OpenRead(filePath))
            hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        var records = (pack.Broadcasts ?? new()).Concat(pack.MissingBroadcasts ?? new()).ToArray();
        var matched = 0;
        var missing = 0;
        var ambiguous = 0;

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        for (var index = 0; index < records.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = records[index];
            NormalizeIncomingItem(item);
            var collectionId = await ResolveCollectionIdAsync(connection, item.Show, pack.Manifest.Show, cancellationToken).ConfigureAwait(false);
            if (!collectionId.HasValue)
            {
                missing++;
                continue;
            }
            var matches = await FindEpisodeMatchesAsync(connection, collectionId.Value, item, cancellationToken).ConfigureAwait(false);
            if (matches.Count == 1) matched++;
            else if (matches.Count > 1) ambiguous++;
            else missing++;
            if (index == 0 || index == records.Length - 1 || (index + 1) % 25 == 0)
                progress?.Report(new ResearchPackTransferProgress(
                    10 + (records.Length == 0 ? 0 : (index + 1) * 88d / records.Length),
                    "Comparing Knowledge records with the archive…",
                    index + 1,
                    records.Length,
                    "Preview"));
        }

        _pending = new PendingImport(filePath, hash, pack);
        var name = Path.GetFileName(filePath);
        var authoritativeAudit = records.Any(item => item.ImportPolicy?.AuthoritativeAudit == true);
        var transcriptCount = pack.Transcripts?.Count ?? 0;
        progress?.Report(new ResearchPackTransferProgress(100, "Knowledge Database analysis complete.", records.Length, records.Length, "Preview"));
        return new ResearchPackPreviewSummary(name, Clean(pack.Manifest.Show), records.Length, transcriptCount, matched, missing, ambiguous,
            authoritativeAudit,
            $"{records.Length:N0} records · {transcriptCount:N0} transcripts · {matched:N0} exact matches · {missing:N0} retained research gaps" +
            (ambiguous > 0 ? $" · {ambiguous:N0} need review" : string.Empty) +
            (authoritativeAudit ? " · authoritative audited replacement" : string.Empty));
    }

    public async Task<ResearchPackApplySummary> ApplyImportAsync(
        IProgress<ResearchPackTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pending = _pending ?? throw new InvalidOperationException("Choose and preview a Knowledge Database before importing it.");
        var pack = pending.Pack;
        var operationProgress = new Progress<ResearchPackOperationProgress>(value =>
            progress?.Report(new ResearchPackTransferProgress(
                2 + value.Percent * 0.81,
                value.Message,
                value.Current,
                value.Total,
                "Import")));
        progress?.Report(new ResearchPackTransferProgress(2, "Creating a pre-import safety snapshot…", Phase: "Import"));
        var result = await Task.Run(
            () => new DatabaseService(_database).ImportKnowledgePack(pack, pending.Path, operationProgress, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        WikiPackImportResult? wiki = null;
        if (pack.Wiki is not null)
        {
            progress?.Report(new ResearchPackTransferProgress(85, $"Importing {pack.Wiki.Pages.Count:N0} Explore pages and embedded images…", 0, pack.Wiki.Pages.Count, "Explore"));
            var wikiProgress = new Progress<WikiPackOperationProgress>(value =>
                progress?.Report(new ResearchPackTransferProgress(
                    85 + value.Percent * 0.13,
                    value.Message,
                    value.Current,
                    value.Total,
                    "Explore")));
            wiki = await new WikiService(_database)
                .ApplyImportAsync(
                    pack.Wiki,
                    Path.GetFileName(pending.Path),
                    pending.Hash,
                    cancellationToken: cancellationToken,
                    progress: wikiProgress)
                .ConfigureAwait(false);
        }

        _pending = null;
        progress?.Report(new ResearchPackTransferProgress(100, "Knowledge Database import complete.", result.Total, result.Total, "Import"));
        var wikiPagesChanged = wiki is null ? 0 : wiki.CreatedPages + wiki.UpdatedPages;
        return new ResearchPackApplySummary(
            result.ResearchRecordsStored,
            result.Matched,
            result.RetainedMissing,
            result.ConflictsCreated,
            $"Imported {result.ResearchRecordsStored:N0} Knowledge records · {result.Matched:N0} attached · {result.RetainedMissing:N0} retained as archive gaps" +
            (wikiPagesChanged > 0 ? $" · {wikiPagesChanged:N0} Explore pages changed" : string.Empty),
            wikiPagesChanged,
            wiki?.SkippedConflicts ?? 0);
    }

    private async Task<ResearchPackApplySummary> ApplyImportLegacyAsync(
        IProgress<ResearchPackTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pending = _pending ?? throw new InvalidOperationException("Choose and preview a research pack before importing it.");
        var pack = pending.Pack;
        progress?.Report(new ResearchPackTransferProgress(2, "Creating a pre-import safety backup…", Phase: "Import"));
        CreateResearchImportBackup();
        var all = (pack.Broadcasts ?? new()).Select(x => (Item: x, ExplicitMissing: false))
            .Concat((pack.MissingBroadcasts ?? new()).Select(x => (Item: x, ExplicitMissing: true))).ToArray();
        var imported = 0;
        var matched = 0;
        var missing = 0;
        var conflicts = 0;

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionBase = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var transaction = (SqliteTransaction)transactionBase;
        var now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var importRunId = await CreateImportRunAsync(connection, transaction, pending, pack, now, cancellationToken).ConfigureAwait(false);

        for (var index = 0; index < all.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index == 0 || index == all.Length - 1 || (index + 1) % 10 == 0)
                progress?.Report(new ResearchPackTransferProgress(
                    5 + (all.Length == 0 ? 0 : (index + 1) * 78d / all.Length),
                    "Applying transactional merge decisions…",
                    index + 1,
                    all.Length,
                    "Import"));
            var entry = all[index];
            var item = entry.Item;
            NormalizeIncomingItem(item);
            var collectionId = await ResolveCollectionIdAsync(connection, item.Show, pack.Manifest.Show, cancellationToken, transaction).ConfigureAwait(false);
            if (!collectionId.HasValue)
            {
                missing++;
                continue;
            }

            var episodeMatches = await FindEpisodeMatchesAsync(connection, collectionId.Value, item, cancellationToken, transaction).ConfigureAwait(false);
            if (episodeMatches.Count > 1)
            {
                conflicts++;
                continue;
            }
            var episodeId = episodeMatches.Count == 1 ? episodeMatches[0] : (long?)null;
            if (episodeId.HasValue) matched++; else missing++;

            var identity = BuildIdentityKey(collectionId.Value, item);
            var existing = await ReadExistingResearchAsync(connection, transaction, identity, cancellationToken).ConfigureAwait(false);
            var recordIsAuthoritativeAudit = item.ImportPolicy?.AuthoritativeAudit == true;
            var protectManual = existing?.UserModified == true && !recordIsAuthoritativeAudit;
            if (protectManual && HasMaterialDifference(existing!, item)) conflicts++;

            var researchId = await UpsertResearchAsync(connection, transaction, importRunId, collectionId.Value,
                episodeId, identity, item, entry.ExplicitMissing, recordIsAuthoritativeAudit, now, cancellationToken).ConfigureAwait(false);
            await MergeRelatedResearchAsync(connection, transaction, researchId, item, recordIsAuthoritativeAudit, now, cancellationToken).ConfigureAwait(false);
            if (episodeId.HasValue && !protectManual)
                await ApplyEpisodeMetadataAsync(connection, transaction, episodeId.Value, item, recordIsAuthoritativeAudit, now, cancellationToken).ConfigureAwait(false);
            if (recordIsAuthoritativeAudit)
                await ResolveConflictsSupersededByAuthoritativeAuditAsync(connection, transaction, researchId, now, cancellationToken).ConfigureAwait(false);
            imported++;
        }

        await using (var finish = connection.CreateCommand())
        {
            finish.Transaction = transaction;
            finish.CommandText = """
                UPDATE research_import_runs SET imported_count=$imported,matched_count=$matched,
                    missing_count=$missing,conflict_count=$conflicts,status='completed',
                    summary_json=$summary WHERE id=$id;
                """;
            finish.Parameters.AddWithValue("$imported", imported);
            finish.Parameters.AddWithValue("$matched", matched);
            finish.Parameters.AddWithValue("$missing", missing);
            finish.Parameters.AddWithValue("$conflicts", conflicts);
            finish.Parameters.AddWithValue("$summary", $"{{\"imported\":{imported},\"matched\":{matched},\"missing\":{missing},\"conflicts\":{conflicts}}}");
            finish.Parameters.AddWithValue("$id", importRunId);
            await finish.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        if (pack.Wiki is not null)
        {
            progress?.Report(new ResearchPackTransferProgress(85, $"Importing {pack.Wiki.Pages.Count:N0} Explore pages and embedded images…", 0, pack.Wiki.Pages.Count, "Explore"));
            var wikiProgress = new Progress<WikiPackOperationProgress>(value =>
                progress?.Report(new ResearchPackTransferProgress(
                    85 + value.Percent * 0.13,
                    value.Message,
                    value.Current,
                    value.Total,
                    "Explore")));
            await new WikiService(_database)
                .ApplyImportAsync(
                    pack.Wiki,
                    Path.GetFileName(pending.Path),
                    pending.Hash,
                    cancellationToken: cancellationToken,
                    progress: wikiProgress)
                .ConfigureAwait(false);
        }
        _pending = null;
        var containsAuthoritativeAudit = all.Any(entry => entry.Item.ImportPolicy?.AuthoritativeAudit == true);
        progress?.Report(new ResearchPackTransferProgress(100, "Knowledge Database import complete.", all.Length, all.Length, "Import"));
        return new ResearchPackApplySummary(imported, matched, missing, conflicts,
            $"Imported {imported:N0} research records · {matched:N0} attached · {missing:N0} retained as archive gaps" +
            (containsAuthoritativeAudit ? " · authoritative audited replacement applied" : string.Empty) +
            (conflicts > 0 ? $" · {conflicts:N0} ambiguous identities retained for review" : string.Empty));
    }

    public Task CancelImportAsync(CancellationToken cancellationToken = default)
    {
        _pending = null;
        return Task.CompletedTask;
    }

    public async Task<ResearchPackExportSummary> ExportAsync(CancellationToken cancellationToken = default)
    {
        var database = new DatabaseService(_database);
        var pack = await Task.Run(
            () => database.BuildCompleteKnowledgePack(AppVersionService.Version),
            cancellationToken).ConfigureAwait(false);
        var databaseName = Path.GetFileName(_database.DatabasePath);
        var databaseIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(databaseName)))[..16].ToLowerInvariant();
        pack.Wiki = await new WikiService(_database)
            .GetAuthoringSnapshotAsync(AppVersionService.Version, databaseIdentity, cancellationToken)
            .ConfigureAwait(false);
        var bytes = _packs.ExportBytes(pack);
        return new ResearchPackExportSummary(bytes, "RadioVault-Archive-Knowledge.trvknowledge",
            pack.Broadcasts.Count, pack.MissingBroadcasts.Count, pack.Transcripts.Count, pack.Wiki.Pages.Count);
    }

    private async Task<ResearchPackExportSummary> ExportScopedAsync(int collectionId, string collectionName, int? year, CancellationToken cancellationToken = default)
    {
        var pack = new TrvKnowledgePack
        {
            Manifest = new TrvPackManifest
            {
                AppVersion = AppVersionService.Version,
                Show = collectionName,
                Year = year
            }
        };
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var family = CollectionIdentityResolver.ResolveFamily(connection, collectionId);
        var collectionIds = family?.CollectionIds ?? new[] { collectionId };
        collectionName = family?.CanonicalName ?? collectionName;
        pack.Manifest.Show = collectionName;
        await using var command = connection.CreateCommand();
        var collectionPredicate = CollectionIdentityResolver.AddIdPredicate(
            command, "collection_id", "exportCollection", collectionIds);
        command.CommandText = $"""
            SELECT id,episode_id,source_broadcast_id,air_date,slot,part_number,total_parts,
                   headline,summary,station,edition,broadcast_variant,broadcast_era,episode_type,
                   archive_notes,confidence,confidence_reason,research_json,
                   COALESCE((SELECT mf.original_filename FROM media_files mf
                             WHERE mf.episode_id=research_broadcasts.episode_id AND COALESCE(mf.is_missing,0)=0
                             ORDER BY COALESCE(mf.is_preferred,0) DESC,mf.id LIMIT 1),'')
            FROM research_broadcasts
            WHERE ({collectionPredicate}) AND ($year IS NULL OR CAST(substr(air_date,1,4) AS INTEGER)=$year)
            ORDER BY air_date,slot,part_number,id;
            """;
        command.Parameters.AddWithValue("$year", year.HasValue ? year.Value : DBNull.Value);
        var rows = new List<ExportRow>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new ExportRow(
                    reader.GetInt64(0), reader.IsDBNull(1), reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    reader.IsDBNull(3) ? string.Empty : reader.GetString(3), reader.IsDBNull(4) ? string.Empty : reader.GetString(4),
                    reader.GetInt32(5), reader.IsDBNull(6) ? null : reader.GetInt32(6),
                    reader.IsDBNull(7) ? string.Empty : reader.GetString(7), reader.IsDBNull(8) ? string.Empty : reader.GetString(8),
                    reader.IsDBNull(9) ? string.Empty : reader.GetString(9), reader.IsDBNull(10) ? string.Empty : reader.GetString(10),
                    reader.IsDBNull(11) ? string.Empty : reader.GetString(11), reader.IsDBNull(12) ? string.Empty : reader.GetString(12),
                    reader.IsDBNull(13) ? string.Empty : reader.GetString(13), reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
                    reader.GetInt32(15), reader.IsDBNull(16) ? string.Empty : reader.GetString(16),
                    reader.IsDBNull(17) ? string.Empty : reader.GetString(17),
                    reader.IsDBNull(18) ? string.Empty : reader.GetString(18)));
            }
        }
        foreach (var row in rows)
        {
            var item = DeserializeBroadcastSafely(row.ResearchJson);
            item.BroadcastId = FirstNonEmpty(item.BroadcastId, row.BroadcastId);
            item.Show = collectionName;
            item.BroadcastDate = FirstNonEmpty(item.BroadcastDate, row.AirDate);
            item.Slot = FirstNonEmpty(item.Slot, row.Slot);
            item.PartNumber = Math.Max(1, row.PartNumber);
            item.TotalParts = row.TotalParts;
            item.Research ??= new TrvPackResearch();
            item.Research.Headline = FirstNonEmpty(item.Research.Headline, row.Headline);
            item.Research.Summary = FirstNonEmpty(item.Research.Summary, row.Summary);
            item.Research.Broadcast ??= new TrvPackBroadcastMetadata();
            item.Research.Broadcast.Station = FirstNonEmpty(item.Research.Broadcast.Station, row.Station);
            item.Research.Edition = FirstNonEmpty(item.Research.Edition, row.Edition);
            item.Research.Broadcast.Variant = FirstNonEmpty(item.Research.Broadcast.Variant, row.Variant);
            item.Research.Broadcast.Era = FirstNonEmpty(item.Research.Broadcast.Era, row.Era);
            item.Research.Broadcast.EpisodeType = FirstNonEmpty(item.Research.Broadcast.EpisodeType, row.EpisodeType);
            item.Research.ArchiveNotes = FirstNonEmpty(item.Research.ArchiveNotes, row.ArchiveNotes);
            item.Research.Quality ??= new TrvPackResearchQuality();
            item.Research.Quality.Confidence = row.Confidence;
            item.Research.Quality.ConfidenceReason = FirstNonEmpty(item.Research.Quality.ConfidenceReason, row.ConfidenceReason);
            item.Research.Catalogue ??= new TrvPackCatalogueMetadata();
            if (KnownShowCatalog.SupportsUndatedCatalogueItems(collectionName))
            {
                item.Research.Catalogue.Series = FirstNonEmpty(item.Research.Catalogue.Series, collectionName);
                var dateHint = CatalogueDateService.Resolve(
                    row.AirDate,
                    item.Research.Catalogue.OriginalReleaseDate,
                    item.Research.Catalogue.RecordingDate,
                    row.OriginalFilename,
                    row.Headline);
                item.Research.Catalogue.OriginalReleaseDate = FirstNonEmpty(
                    item.Research.Catalogue.OriginalReleaseDate,
                    row.AirDate,
                    dateHint.DisplayText);
                item.Research.Catalogue.OriginalFilename = FirstNonEmpty(item.Research.Catalogue.OriginalFilename, row.OriginalFilename);
                item.Research.Catalogue.Network = FirstNonEmpty(item.Research.Catalogue.Network, row.Station);
                item.Research.Catalogue.Programme = FirstNonEmpty(item.Research.Catalogue.Programme, row.Edition);
                item.Research.Catalogue.Format = FirstNonEmpty(item.Research.Catalogue.Format, row.EpisodeType);
            }
            await EnrichRelatedAsync(connection, row.ResearchId, item, cancellationToken).ConfigureAwait(false);
            if (row.IsMissing) pack.MissingBroadcasts.Add(item); else pack.Broadcasts.Add(item);
        }
        await AppendUnresearchedLibraryEpisodesAsync(
            connection,
            collectionIds,
            collectionName,
            year,
            pack,
            cancellationToken).ConfigureAwait(false);
        await AppendTranscriptsAsync(
            connection,
            collectionIds,
            collectionName,
            year,
            pack,
            cancellationToken).ConfigureAwait(false);

        pack.Manifest.BroadcastCount = pack.Broadcasts.Count;
        pack.Manifest.MissingBroadcastCount = pack.MissingBroadcasts.Count;
        pack.Manifest.TranscriptCount = pack.Transcripts.Count;
        var databaseName = Path.GetFileName(_database.DatabasePath);
        var databaseIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(databaseName)))[..16].ToLowerInvariant();
        pack.Wiki = await new WikiService(_database)
            .GetAuthoringSnapshotAsync(AppVersionService.Version, databaseIdentity, cancellationToken)
            .ConfigureAwait(false);
        var bytes = _packs.ExportBytes(pack);
        var suffix = year.HasValue ? $"-{year.Value}" : "-All";
        return new ResearchPackExportSummary(bytes, $"{SanitizeFileName(collectionName)}{suffix}.trvknowledge",
            pack.Broadcasts.Count, pack.MissingBroadcasts.Count, pack.Transcripts.Count, pack.Wiki.Pages.Count);
    }

    private static async Task AppendTranscriptsAsync(
        SqliteConnection connection,
        IReadOnlyList<int> collectionIds,
        string collectionName,
        int? year,
        TrvKnowledgePack pack,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var collectionPredicate = CollectionIdentityResolver.AddIdPredicate(
            command, "e.collection_id", "transcriptExportCollection", collectionIds);
        command.CommandText = $"""
            SELECT t.id,COALESCE(e.broadcast_uid,''),e.air_date,COALESCE(e.part_number,1),
                   t.status,t.language,t.engine_id,t.model_id,t.full_text,t.has_speaker_diarization
              FROM transcripts t
              JOIN episodes e ON e.id=t.episode_id
             WHERE ({collectionPredicate})
               AND COALESCE(e.hidden,0)=0
               AND ($year IS NULL OR CAST(substr(e.air_date,1,4) AS INTEGER)=$year)
             ORDER BY e.air_date,e.part_number,t.id;
            """;
        command.Parameters.AddWithValue("$year", year.HasValue ? year.Value : DBNull.Value);
        var transcripts = new List<(long Id, TrvPackTranscript Transcript)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                transcripts.Add((reader.GetInt64(0), new TrvPackTranscript
                {
                    BroadcastId = reader.GetString(1),
                    Show = collectionName,
                    BroadcastDate = reader.IsDBNull(2) ? null : reader.GetString(2),
                    PartNumber = Math.Max(1, reader.GetInt32(3)),
                    Status = reader.GetString(4),
                    Language = reader.GetString(5),
                    Engine = reader.GetString(6),
                    Model = reader.GetString(7),
                    FullText = reader.GetString(8),
                    HasSpeakerDiarization = reader.GetInt32(9) == 1
                }));
            }
        }

        foreach (var item in transcripts)
        {
            await using var segments = connection.CreateCommand();
            segments.CommandText = """
                SELECT segment_index,start_ms,end_ms,COALESCE(speaker,''),COALESCE(speaker_key,''),text
                  FROM transcript_segments
                 WHERE transcript_id=$transcript
                 ORDER BY segment_index;
                """;
            segments.Parameters.AddWithValue("$transcript", item.Id);
            await using var reader = await segments.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                item.Transcript.Segments.Add(new TrvPackTranscriptSegment
                {
                    Index = reader.GetInt32(0),
                    StartMs = reader.GetInt64(1),
                    EndMs = reader.GetInt64(2),
                    Speaker = reader.GetString(3),
                    SpeakerKey = reader.GetString(4),
                    Text = reader.GetString(5)
                });
            }
            pack.Transcripts.Add(item.Transcript);
        }
    }

    private static async Task AppendUnresearchedLibraryEpisodesAsync(
        SqliteConnection connection,
        IReadOnlyList<int> collectionIds,
        string collectionName,
        int? year,
        TrvKnowledgePack pack,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        var collectionPredicate = CollectionIdentityResolver.AddIdPredicate(
            command, "e.collection_id", "libraryExportCollection", collectionIds);
        command.CommandText = $"""
            SELECT e.id,COALESCE(e.broadcast_uid,''),e.air_date,COALESCE(e.broadcast_slot,''),
                   COALESCE(e.part_number,1),e.total_parts,COALESCE(e.title,''),
                   COALESCE(e.description,''),COALESCE(e.metadata_confidence,0),
                   COALESCE(e.metadata_confidence_reason,''),
                   COALESCE((
                       SELECT mf.original_filename
                         FROM media_files mf
                        WHERE mf.episode_id=e.id
                        ORDER BY COALESCE(mf.is_missing,0),COALESCE(mf.is_preferred,0) DESC,mf.id
                        LIMIT 1
                   ),'')
              FROM episodes e
             WHERE ({collectionPredicate})
               AND COALESCE(e.hidden,0)=0
               AND ($year IS NULL OR CAST(substr(e.air_date,1,4) AS INTEGER)=$year)
               AND NOT EXISTS(
                   SELECT 1
                     FROM research_broadcasts rb
                    WHERE rb.episode_id=e.id
                       OR (COALESCE(e.broadcast_uid,'')<>'' AND rb.source_broadcast_id=e.broadcast_uid)
               )
               AND (
                   NOT EXISTS(SELECT 1 FROM episode_canonical_map map WHERE map.episode_id=e.id)
                   OR EXISTS(SELECT 1 FROM episode_canonical_map map WHERE map.episode_id=e.id AND map.is_survivor=1)
               )
             ORDER BY COALESCE(e.air_date,'9999-12-31'),e.title,e.id;
            """;
        command.Parameters.AddWithValue("$year", year.HasValue ? year.Value : DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var episodeId = reader.GetInt64(0);
            var broadcastId = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(broadcastId))
                broadcastId = $"{LibraryTruthIdentity.Normalize(collectionName, "SHOW")}-UNDATED-{episodeId}";

            var originalFilename = reader.GetString(10);
            var airDate = reader.IsDBNull(2) ? null : reader.GetString(2);
            var headline = reader.GetString(6);
            var dateHint = CatalogueDateService.Resolve(airDate, originalFilename, headline);
            pack.Broadcasts.Add(new TrvPackBroadcast
            {
                BroadcastId = broadcastId,
                Show = collectionName,
                BroadcastDate = airDate,
                Slot = reader.GetString(3),
                PartNumber = Math.Max(1, reader.GetInt32(4)),
                TotalParts = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                Research = new TrvPackResearch
                {
                    Headline = NullIfBlank(headline),
                    Summary = NullIfBlank(reader.GetString(7)),
                    ArchiveNotes = string.IsNullOrWhiteSpace(originalFilename)
                        ? null
                        : $"Original file: {originalFilename}",
                    Catalogue = new TrvPackCatalogueMetadata
                    {
                        Series = KnownShowCatalog.SupportsUndatedCatalogueItems(collectionName) ? collectionName : null,
                        Format = KnownShowCatalog.SupportsUndatedCatalogueItems(collectionName) ? "Archive recording" : null,
                        OriginalReleaseDate = NullIfBlank(FirstNonEmpty(airDate, dateHint.DisplayText)),
                        OriginalFilename = NullIfBlank(originalFilename)
                    },
                    Quality = new TrvPackResearchQuality
                    {
                        Confidence = Math.Clamp(reader.GetInt32(8), 0, 100),
                        ConfidenceReason = NullIfBlank(reader.GetString(9))
                    }
                }
            });
        }
    }

    private static string? NullIfBlank(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task<int?> ResolveCollectionIdAsync(SqliteConnection connection, string? itemShow, string? manifestShow,
        CancellationToken token, SqliteTransaction? transaction = null)
    {
        var show = KnownShowCatalog.Normalize(FirstNonEmpty(itemShow, manifestShow));
        if (string.IsNullOrWhiteSpace(show)) return null;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT c.id FROM collections c
            LEFT JOIN collection_aliases a ON a.collection_id=c.id
            WHERE c.name=$show COLLATE NOCASE OR a.alias=$show COLLATE NOCASE
            ORDER BY CASE WHEN c.name=$show COLLATE NOCASE THEN 0 ELSE 1 END LIMIT 1;
            """;
        command.Parameters.AddWithValue("$show", show.Trim());
        var value = await command.ExecuteScalarAsync(token).ConfigureAwait(false);
        return value is null or DBNull ? null : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task<List<long>> FindEpisodeMatchesAsync(SqliteConnection connection, int collectionId, TrvPackBroadcast item,
        CancellationToken token, SqliteTransaction? transaction = null)
    {
        var result = new List<long>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        if (!string.IsNullOrWhiteSpace(item.BroadcastId))
        {
            command.CommandText = "SELECT id FROM episodes WHERE collection_id=$collection AND broadcast_uid=$uid AND COALESCE(hidden,0)=0";
            command.Parameters.AddWithValue("$uid", item.BroadcastId.Trim());
        }
        else if (!string.IsNullOrWhiteSpace(item.BroadcastDate))
        {
            command.CommandText = """
                SELECT id FROM episodes WHERE collection_id=$collection AND air_date=$date
                  AND COALESCE(broadcast_slot,'')=$slot AND COALESCE(part_number,1)=$part
                  AND COALESCE(hidden,0)=0 ORDER BY id;
                """;
            command.Parameters.AddWithValue("$date", item.BroadcastDate.Trim());
            command.Parameters.AddWithValue("$slot", EffectiveSlot(item));
            command.Parameters.AddWithValue("$part", Math.Max(1, item.PartNumber));
        }
        else return result;
        command.Parameters.AddWithValue("$collection", collectionId);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) result.Add(reader.GetInt64(0));
        return result;
    }

    private static async Task<long> CreateImportRunAsync(SqliteConnection connection, SqliteTransaction transaction,
        PendingImport pending, TrvKnowledgePack pack, string now, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO research_import_runs(package_name,package_sha256,schema_version,app_version,imported_at,status)
            VALUES($name,$hash,$schema,$app,$now,'in_progress'); SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", Path.GetFileName(pending.Path));
        command.Parameters.AddWithValue("$hash", pending.Hash);
        command.Parameters.AddWithValue("$schema", pack.Manifest?.SchemaVersion ?? 0);
        command.Parameters.AddWithValue("$app", Clean(pack.Manifest?.AppVersion));
        command.Parameters.AddWithValue("$now", now);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<ExistingResearch?> ReadExistingResearchAsync(SqliteConnection connection, SqliteTransaction transaction,
        string identity, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,user_modified,headline,summary FROM research_broadcasts WHERE identity_key=$identity";
        command.Parameters.AddWithValue("$identity", identity);
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        return await reader.ReadAsync(token).ConfigureAwait(false)
            ? new ExistingResearch(reader.GetInt64(0), reader.GetInt32(1) == 1, reader.GetString(2), reader.GetString(3))
            : null;
    }

    private static bool HasMaterialDifference(ExistingResearch existing, TrvPackBroadcast item)
        => (!string.IsNullOrWhiteSpace(item.Research?.Headline) && !string.Equals(existing.Headline.Trim(), item.Research.Headline.Trim(), StringComparison.OrdinalIgnoreCase))
           || (!string.IsNullOrWhiteSpace(item.Research?.Summary) && !string.Equals(existing.Summary.Trim(), item.Research.Summary.Trim(), StringComparison.OrdinalIgnoreCase));

    private static async Task<long> UpsertResearchAsync(SqliteConnection connection, SqliteTransaction transaction, long importRunId,
        int collectionId, long? episodeId, string identity, TrvPackBroadcast item, bool explicitMissing, bool authoritativeAudit,
        string now, CancellationToken token)
    {
        var research = item.Research ?? new TrvPackResearch();
        var broadcast = research.Broadcast ?? new TrvPackBroadcastMetadata();
        var quality = research.Quality ?? new TrvPackResearchQuality();
        var state = episodeId.HasValue ? "in_library" : "partially_researched";
        var existence = episodeId.HasValue ? "in_library" : explicitMissing && quality.Confidence >= 80 ? "confirmed_missing" : explicitMissing ? "probable_missing" : "unknown_gap";
        var json = KnowledgePackService.SerializeBroadcast(item);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO research_broadcasts(identity_key,collection_id,episode_id,source_broadcast_id,air_date,slot,
                part_number,total_parts,headline,summary,station,edition,broadcast_variant,broadcast_era,episode_type,
                archive_notes,research_json,research_state,existence_status,confidence,confidence_reason,user_modified,
                needs_review,import_run_id,attached_at,created_at,updated_at)
            VALUES($identity,$collection,$episode,$uid,$date,$slot,$part,$total,$headline,$summary,$station,$edition,
                $variant,$era,$type,$notes,$json,$state,$existence,$confidence,$reason,0,0,$run,$attached,$now,$now)
            ON CONFLICT(identity_key) DO UPDATE SET
                episode_id=COALESCE(excluded.episode_id,research_broadcasts.episode_id),
                source_broadcast_id=CASE WHEN research_broadcasts.source_broadcast_id='' THEN excluded.source_broadcast_id ELSE research_broadcasts.source_broadcast_id END,
                headline=CASE WHEN $authoritative=1 THEN excluded.headline WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.headline ELSE excluded.headline END,
                summary=CASE WHEN $authoritative=1 THEN excluded.summary WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.summary ELSE excluded.summary END,
                station=CASE WHEN $authoritative=1 THEN excluded.station WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.station ELSE excluded.station END,
                edition=CASE WHEN $authoritative=1 THEN excluded.edition WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.edition ELSE excluded.edition END,
                broadcast_variant=CASE WHEN $authoritative=1 THEN excluded.broadcast_variant WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.broadcast_variant ELSE excluded.broadcast_variant END,
                broadcast_era=CASE WHEN $authoritative=1 THEN excluded.broadcast_era WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.broadcast_era ELSE excluded.broadcast_era END,
                episode_type=CASE WHEN $authoritative=1 THEN excluded.episode_type WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.episode_type ELSE excluded.episode_type END,
                archive_notes=CASE WHEN $authoritative=1 THEN excluded.archive_notes WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.archive_notes ELSE excluded.archive_notes END,
                research_json=CASE WHEN $authoritative=1 THEN excluded.research_json WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.research_json ELSE excluded.research_json END,
                research_state=excluded.research_state,existence_status=excluded.existence_status,
                confidence=CASE WHEN $authoritative=1 THEN excluded.confidence WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.confidence ELSE excluded.confidence END,
                confidence_reason=CASE WHEN $authoritative=1 THEN excluded.confidence_reason WHEN research_broadcasts.user_modified=1 THEN research_broadcasts.confidence_reason ELSE excluded.confidence_reason END,
                user_modified=CASE WHEN $authoritative=1 THEN 0 ELSE research_broadcasts.user_modified END,
                import_run_id=excluded.import_run_id,attached_at=excluded.attached_at,updated_at=excluded.updated_at
            RETURNING id;
            """;
        command.Parameters.AddWithValue("$authoritative", authoritativeAudit ? 1 : 0);
        command.Parameters.AddWithValue("$identity", identity);
        command.Parameters.AddWithValue("$collection", collectionId);
        command.Parameters.AddWithValue("$episode", episodeId.HasValue ? episodeId.Value : DBNull.Value);
        command.Parameters.AddWithValue("$uid", Clean(item.BroadcastId));
        command.Parameters.AddWithValue("$date", string.IsNullOrWhiteSpace(item.BroadcastDate) ? DBNull.Value : item.BroadcastDate.Trim());
        command.Parameters.AddWithValue("$slot", EffectiveSlot(item));
        command.Parameters.AddWithValue("$part", Math.Max(1, item.PartNumber));
        command.Parameters.AddWithValue("$total", item.TotalParts.HasValue ? item.TotalParts.Value : DBNull.Value);
        command.Parameters.AddWithValue("$headline", Clean(research.Headline));
        command.Parameters.AddWithValue("$summary", Clean(research.Summary));
        command.Parameters.AddWithValue("$station", Clean(broadcast.Station));
        command.Parameters.AddWithValue("$edition", FirstNonEmpty(research.Edition, broadcast.Station));
        command.Parameters.AddWithValue("$variant", Clean(broadcast.Variant));
        command.Parameters.AddWithValue("$era", Clean(broadcast.Era));
        command.Parameters.AddWithValue("$type", Clean(broadcast.EpisodeType));
        command.Parameters.AddWithValue("$notes", Clean(research.ArchiveNotes));
        command.Parameters.AddWithValue("$json", json);
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$existence", existence);
        command.Parameters.AddWithValue("$confidence", Math.Clamp(quality.Confidence, 0, 100));
        command.Parameters.AddWithValue("$reason", Clean(quality.ConfidenceReason));
        command.Parameters.AddWithValue("$run", importRunId);
        command.Parameters.AddWithValue("$attached", episodeId.HasValue ? now : DBNull.Value);
        command.Parameters.AddWithValue("$now", now);
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task MergeRelatedResearchAsync(SqliteConnection connection, SqliteTransaction transaction, long researchId,
        TrvPackBroadcast item, bool authoritativeAudit, string now, CancellationToken token)
    {
        if (authoritativeAudit)
        {
            foreach (var table in new[] { "research_sources", "research_people", "research_topics", "research_moments" })
            {
                await using var clear = connection.CreateCommand();
                clear.Transaction = transaction;
                clear.CommandText = $"DELETE FROM {table} WHERE research_broadcast_id=$id";
                clear.Parameters.AddWithValue("$id", researchId);
                await clear.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }

        var research = item.Research ?? new TrvPackResearch();
        var quality = Math.Clamp(research.Quality?.Confidence ?? 0, 0, 100);
        var people = research.People ?? new TrvPackPeople();
        foreach (var (role, names) in new[]
                 {
                     ("host", people.Hosts), ("guest", people.Guests.Concat(research.Guests ?? new()).ToList()),
                     ("caller", people.Callers), ("mentioned", people.MentionedPeople)
                 })
        {
            foreach (var name in Normalize(names))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT OR IGNORE INTO research_people(research_broadcast_id,name,role,confidence,created_at) VALUES($id,$name,$role,$confidence,$now)";
                command.Parameters.AddWithValue("$id", researchId);
                command.Parameters.AddWithValue("$name", name);
                command.Parameters.AddWithValue("$role", role);
                command.Parameters.AddWithValue("$confidence", quality);
                command.Parameters.AddWithValue("$now", now);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
        }
        foreach (var topic in Normalize(research.Topics))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR IGNORE INTO research_topics(research_broadcast_id,topic,confidence,created_at) VALUES($id,$topic,$confidence,$now)";
            command.Parameters.AddWithValue("$id", researchId);
            command.Parameters.AddWithValue("$topic", topic);
            command.Parameters.AddWithValue("$confidence", quality);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        foreach (var source in item.Sources ?? new())
        {
            if (string.IsNullOrWhiteSpace(source.Url) && string.IsNullOrWhiteSpace(source.Title)) continue;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO research_sources(research_broadcast_id,url,title,publisher,source_type,accessed_at,
                    confidence,supports,notes,created_at)
                VALUES($id,$url,$title,$publisher,'community',$accessed,$confidence,$supports,$notes,$now);
                """;
            command.Parameters.AddWithValue("$id", researchId);
            command.Parameters.AddWithValue("$url", Clean(source.Url));
            command.Parameters.AddWithValue("$title", Clean(source.Title));
            command.Parameters.AddWithValue("$publisher", Clean(source.Publisher));
            command.Parameters.AddWithValue("$accessed", string.IsNullOrWhiteSpace(source.Accessed) ? DBNull.Value : source.Accessed.Trim());
            command.Parameters.AddWithValue("$confidence", quality);
            command.Parameters.AddWithValue("$supports", string.Join("|", Normalize(source.Supports)));
            command.Parameters.AddWithValue("$notes", Clean(source.Notes));
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        foreach (var moment in research.Moments ?? new())
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT OR IGNORE INTO research_moments(research_broadcast_id,timestamp_seconds,title,description,tags,confidence,created_at)
                VALUES($id,$time,$title,$description,$tags,$confidence,$now);
                """;
            command.Parameters.AddWithValue("$id", researchId);
            command.Parameters.AddWithValue("$time", Math.Max(0, moment.TimestampSeconds));
            command.Parameters.AddWithValue("$title", Clean(moment.Title));
            command.Parameters.AddWithValue("$description", Clean(moment.Description));
            command.Parameters.AddWithValue("$tags", string.Join("|", Normalize(moment.Tags)));
            command.Parameters.AddWithValue("$confidence", quality);
            command.Parameters.AddWithValue("$now", now);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task ApplyEpisodeMetadataAsync(SqliteConnection connection, SqliteTransaction transaction, long episodeId,
        TrvPackBroadcast item, bool authoritativeAudit, string now, CancellationToken token)
    {
        var research = item.Research ?? new TrvPackResearch();
        var people = research.People ?? new TrvPackPeople();
        var broadcast = research.Broadcast ?? new TrvPackBroadcastMetadata();
        var sourceUrls = Normalize(item.Sources?.Select(source => source.Url) ?? Array.Empty<string>()).ToArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE episodes SET
                title=CASE WHEN $authoritative=1 THEN $headline WHEN trim($headline)<>'' THEN $headline ELSE title END,
                description=CASE WHEN $authoritative=1 THEN $summary WHEN trim($summary)<>'' THEN $summary ELSE description END,
                edition=CASE WHEN $authoritative=1 THEN $edition WHEN trim($edition)<>'' THEN $edition ELSE edition END,
                archive_notes=CASE WHEN $authoritative=1 THEN $notes WHEN trim($notes)<>'' THEN $notes ELSE archive_notes END,
                broadcast_slot=CASE WHEN $authoritative=1 THEN $slot ELSE broadcast_slot END,
                broadcast_variant=CASE WHEN $authoritative=1 THEN $variant WHEN trim($variant)<>'' THEN $variant ELSE broadcast_variant END,
                broadcast_era=CASE WHEN $authoritative=1 THEN $era WHEN trim($era)<>'' THEN $era ELSE broadcast_era END,
                episode_type=CASE WHEN $authoritative=1 THEN $type WHEN trim($type)<>'' THEN $type ELSE episode_type END,
                hosts=CASE WHEN $authoritative=1 THEN $hosts WHEN trim($hosts)<>'' THEN $hosts ELSE hosts END,
                callers=CASE WHEN $authoritative=1 THEN $callers WHEN trim($callers)<>'' THEN $callers ELSE callers END,
                mentioned_people=CASE WHEN $authoritative=1 THEN $mentioned WHEN trim($mentioned)<>'' THEN $mentioned ELSE mentioned_people END,
                metadata_confidence=CASE WHEN $authoritative=1 THEN $confidence ELSE MAX(metadata_confidence,$confidence) END,
                metadata_confidence_reason=CASE WHEN $authoritative=1 THEN $reason WHEN trim($reason)<>'' THEN $reason ELSE metadata_confidence_reason END,
                research_sources=CASE WHEN $authoritative=1 THEN $sources ELSE research_sources END,
                updated_at=$now
            WHERE id=$id AND ($authoritative=1 OR COALESCE(user_modified,0)=0);
            """;
        command.Parameters.AddWithValue("$authoritative", authoritativeAudit ? 1 : 0);
        command.Parameters.AddWithValue("$headline", Clean(research.Headline));
        command.Parameters.AddWithValue("$summary", Clean(research.Summary));
        command.Parameters.AddWithValue("$edition", FirstNonEmpty(research.Edition, broadcast.Station));
        command.Parameters.AddWithValue("$notes", Clean(research.ArchiveNotes));
        command.Parameters.AddWithValue("$slot", FirstNonEmpty(broadcast.Slot, item.Slot));
        command.Parameters.AddWithValue("$variant", Clean(broadcast.Variant));
        command.Parameters.AddWithValue("$era", Clean(broadcast.Era));
        command.Parameters.AddWithValue("$type", Clean(broadcast.EpisodeType));
        command.Parameters.AddWithValue("$hosts", string.Join("|", Normalize(people.Hosts)));
        command.Parameters.AddWithValue("$callers", string.Join("|", Normalize(people.Callers)));
        command.Parameters.AddWithValue("$mentioned", string.Join("|", Normalize(people.MentionedPeople)));
        command.Parameters.AddWithValue("$confidence", Math.Clamp(research.Quality?.Confidence ?? 0, 0, 100));
        command.Parameters.AddWithValue("$reason", Clean(research.Quality?.ConfidenceReason));
        command.Parameters.AddWithValue("$sources", string.Join("\n", sourceUrls));
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", episodeId);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        await ApplyCatalogueDateAsync(connection, transaction, episodeId, item, now, token).ConfigureAwait(false);

        var guests = Normalize(people.Guests.Concat(research.Guests ?? new List<string>()));
        var topics = Normalize(research.Topics);
        if (authoritativeAudit)
        {
            await ReplaceEpisodeNamesAsync(connection, transaction, episodeId, "guests", "episode_guests", "guest_id", guests, token).ConfigureAwait(false);
            await ReplaceEpisodeNamesAsync(connection, transaction, episodeId, "tags", "episode_tags", "tag_id", topics, token).ConfigureAwait(false);
        }
    }

    private static async Task ApplyCatalogueDateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long episodeId,
        TrvPackBroadcast item,
        string now,
        CancellationToken token)
    {
        if (!KnownShowCatalog.SupportsDateReview(item.Show)) return;

        var catalogue = item.Research?.Catalogue;
        var decision = catalogue?.DateReviewStatus?.Trim() ?? string.Empty;
        var dateHint = CatalogueDateService.Resolve(
            catalogue?.DateReviewDate,
            item.BroadcastDate,
            catalogue?.OriginalReleaseDate,
            catalogue?.RecordingDate);
        var releaseHint = CatalogueDateService.Resolve(catalogue?.OriginalReleaseDate);
        var recordingHint = CatalogueDateService.Resolve(catalogue?.RecordingDate);

        DateOnly? currentDate = null;
        var currentConfidence = "Unknown";
        await using (var readCurrent = connection.CreateCommand())
        {
            readCurrent.Transaction = transaction;
            readCurrent.CommandText = "SELECT air_date,COALESCE(date_confidence,'Unknown') FROM episodes WHERE id=$id";
            readCurrent.Parameters.AddWithValue("$id", episodeId);
            await using var reader = await readCurrent.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                currentDate = reader.IsDBNull(0)
                    ? null
                    : CatalogueDateService.ResolveExactDate(reader.GetString(0));
                currentConfidence = reader.GetString(1);
            }
        }

        var isApproved = decision.Equals("approved_library_date", StringComparison.OrdinalIgnoreCase);
        var isRecordingOnly = decision.Equals("recording_date_only", StringComparison.OrdinalIgnoreCase);
        var isReleaseOnly = decision.Equals("release_date_only", StringComparison.OrdinalIgnoreCase);
        var isLeftUndated = decision.Equals("left_undated", StringComparison.OrdinalIgnoreCase);
        var isKeepExisting = decision.Equals("kept_existing", StringComparison.OrdinalIgnoreCase);
        var isIgnored = decision.Equals("ignored", StringComparison.OrdinalIgnoreCase);
        var isKnownResolvedDecision = isApproved || isRecordingOnly || isReleaseOnly || isLeftUndated
            || isKeepExisting || isIgnored;
        var isExplicitPending = decision.Equals("pending", StringComparison.OrdinalIgnoreCase)
            || decision.Equals("reopened", StringComparison.OrdinalIgnoreCase);
        var hasExplicitDecision = !string.IsNullOrWhiteSpace(decision);
        var uncertainCurrentDate = !currentDate.HasValue || IsUncertainDateConfidence(currentConfidence);
        var autoResearchDate = IsResearchAdoptedDateConfidence(currentConfidence);
        var conflictsWithCurrentDate = DateHintConflictsWithCurrentDate(dateHint, currentDate);
        var hasRoleConflict = DateHintConflictsWithCurrentDate(releaseHint, currentDate)
            || DateHintConflictsWithCurrentDate(recordingHint, currentDate);
        var catalogueDateNeedsConfirmation = KnownShowCatalog.SupportsUndatedCatalogueItems(item.Show)
            && dateHint.HasValue;

        var isPending = isExplicitPending
            || (isApproved && !dateHint.ExactDate.HasValue)
            || (hasExplicitDecision && !isKnownResolvedDecision);
        if (!hasExplicitDecision)
        {
            isPending = uncertainCurrentDate
                || autoResearchDate
                || conflictsWithCurrentDate
                || hasRoleConflict
                || catalogueDateNeedsConfirmation;
            if (!isPending) return;
        }

        if (isPending)
        {
            await using var markForReview = connection.CreateCommand();
            markForReview.Transaction = transaction;
            markForReview.CommandText = """
                UPDATE research_broadcasts
                   SET needs_review=1,research_state='conflicting_information',updated_at=$now
                 WHERE episode_id=$id;
                """;
            markForReview.Parameters.AddWithValue("$now", now);
            markForReview.Parameters.AddWithValue("$id", episodeId);
            await markForReview.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            return;
        }

        if (!isApproved)
        {
            var previousDate = CatalogueDateService.ResolveExactDate(catalogue?.DateReviewPreviousAirDate);
            var previousWasResearchAdopted = IsResearchAdoptedDateConfidence(catalogue?.DateReviewPreviousConfidence);
            var preserveExisting = isKeepExisting || isIgnored;
            var restorePreviousDate = preserveExisting
                ? currentDate.HasValue
                : (isRecordingOnly || isReleaseOnly) && previousDate.HasValue && !previousWasResearchAdopted;
            var restoredDateText = restorePreviousDate
                ? (preserveExisting ? currentDate!.Value : previousDate!.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : null;
            var restoredConfidence = preserveExisting
                ? currentConfidence
                : Clean(catalogue?.DateReviewPreviousConfidence);

            var dateParameter = restoredDateText is null ? (object)DBNull.Value : restoredDateText;
            await using (var settleResearch = connection.CreateCommand())
            {
                settleResearch.Transaction = transaction;
                settleResearch.CommandText = """
                    UPDATE research_broadcasts
                       SET air_date=$date,updated_at=$now
                     WHERE episode_id=$id;
                    """;
                settleResearch.Parameters.AddWithValue("$date", dateParameter);
                settleResearch.Parameters.AddWithValue("$now", now);
                settleResearch.Parameters.AddWithValue("$id", episodeId);
                await settleResearch.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }

            var changedLibraryDate = false;
            await using (var settleEpisode = connection.CreateCommand())
            {
                settleEpisode.Transaction = transaction;
                settleEpisode.CommandText = restorePreviousDate
                    ? """
                        UPDATE episodes
                           SET air_date=$date,
                               date_confidence=CASE WHEN trim($confidence)='' THEN 'Unknown' ELSE $confidence END,
                               user_modified=1,updated_at=$now
                         WHERE id=$id;
                        """
                    : """
                        UPDATE episodes
                           SET air_date=NULL,date_confidence='Unknown',user_modified=1,updated_at=$now
                         WHERE id=$id
                           AND (lower(COALESCE(date_confidence,'')) LIKE 'research exact date%'
                                OR lower(COALESCE(date_confidence,'')) LIKE 'research authoritative%'
                                OR lower(COALESCE(date_confidence,'')) LIKE 'research manual%'
                                OR lower(COALESCE(date_confidence,'')) LIKE 'research date approved%'
                                OR $forceUndated=1);
                        """;
                settleEpisode.Parameters.AddWithValue("$id", episodeId);
                settleEpisode.Parameters.AddWithValue("$now", now);
                if (restorePreviousDate)
                {
                    settleEpisode.Parameters.AddWithValue("$date", restoredDateText!);
                    settleEpisode.Parameters.AddWithValue("$confidence", restoredConfidence);
                }
                else
                {
                    settleEpisode.Parameters.AddWithValue("$forceUndated", isLeftUndated ? 1 : 0);
                }
                changedLibraryDate = await settleEpisode.ExecuteNonQueryAsync(token).ConfigureAwait(false) > 0;
            }

            if (changedLibraryDate)
            {
                await using var settleProjection = connection.CreateCommand();
                settleProjection.Transaction = transaction;
                settleProjection.CommandText = """
                    UPDATE canonical_broadcasts SET air_date=$date
                     WHERE canonical_key IN (SELECT canonical_key FROM episode_canonical_map WHERE episode_id=$id);
                    UPDATE library_truth_broadcasts SET air_date=$date
                     WHERE run_id=(SELECT COALESCE(MAX(id),0) FROM library_truth_runs WHERE status='completed')
                       AND canonical_key IN (SELECT canonical_key FROM episode_canonical_map WHERE episode_id=$id);
                    UPDATE library_truth_files SET current_air_date=$date,proposed_air_date=$date
                     WHERE run_id=(SELECT COALESCE(MAX(id),0) FROM library_truth_runs WHERE status='completed')
                       AND current_episode_id=$id;
                    """;
                settleProjection.Parameters.AddWithValue("$date", dateParameter);
                settleProjection.Parameters.AddWithValue("$id", episodeId);
                await settleProjection.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            return;
        }

        var dateText = dateHint.ExactDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        await using (var updateEpisode = connection.CreateCommand())
        {
            updateEpisode.Transaction = transaction;
            updateEpisode.CommandText = """
                UPDATE episodes
                   SET air_date=$date,date_confidence='Research date approved',user_modified=1,
                       metadata_confidence=MAX(COALESCE(metadata_confidence,0),95),
                       metadata_confidence_reason=CASE
                           WHEN trim(COALESCE(metadata_confidence_reason,''))='' THEN 'Research date approved by the user'
                           ELSE metadata_confidence_reason END,
                       updated_at=$now
                 WHERE id=$id;
                """;
            updateEpisode.Parameters.AddWithValue("$date", dateText);
            updateEpisode.Parameters.AddWithValue("$now", now);
            updateEpisode.Parameters.AddWithValue("$id", episodeId);
            await updateEpisode.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        await using (var updateResearch = connection.CreateCommand())
        {
            updateResearch.Transaction = transaction;
            updateResearch.CommandText = """
                UPDATE research_broadcasts
                   SET air_date=$date,needs_review=0,research_state='in_library',updated_at=$now
                 WHERE episode_id=$id;
                """;
            updateResearch.Parameters.AddWithValue("$date", dateText);
            updateResearch.Parameters.AddWithValue("$now", now);
            updateResearch.Parameters.AddWithValue("$id", episodeId);
            await updateResearch.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        await using (var updateCanonical = connection.CreateCommand())
        {
            updateCanonical.Transaction = transaction;
            updateCanonical.CommandText = """
                UPDATE canonical_broadcasts SET air_date=$date,confidence_score=MAX(COALESCE(confidence_score,0),95)
                 WHERE canonical_key IN (SELECT canonical_key FROM episode_canonical_map WHERE episode_id=$id);
                UPDATE library_truth_broadcasts SET air_date=$date,confidence_score=MAX(COALESCE(confidence_score,0),95)
                 WHERE run_id=(SELECT COALESCE(MAX(id),0) FROM library_truth_runs WHERE status='completed')
                   AND canonical_key IN (SELECT canonical_key FROM episode_canonical_map WHERE episode_id=$id);
                UPDATE library_truth_files
                   SET current_air_date=$date,proposed_air_date=$date,confidence_score=MAX(COALESCE(confidence_score,0),95),confidence='Research approved'
                 WHERE run_id=(SELECT COALESCE(MAX(id),0) FROM library_truth_runs WHERE status='completed')
                   AND current_episode_id=$id;
                """;
            updateCanonical.Parameters.AddWithValue("$date", dateText);
            updateCanonical.Parameters.AddWithValue("$id", episodeId);
            await updateCanonical.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task ReplaceEpisodeNamesAsync(SqliteConnection connection, SqliteTransaction transaction, long episodeId,
        string entityTable, string joinTable, string idColumn, IEnumerable<string> names, CancellationToken token)
    {
        await using (var clear = connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = $"DELETE FROM {joinTable} WHERE episode_id=$episode";
            clear.Parameters.AddWithValue("$episode", episodeId);
            await clear.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        foreach (var name in Normalize(names))
        {
            long entityId;
            await using (var add = connection.CreateCommand())
            {
                add.Transaction = transaction;
                add.CommandText = $"INSERT OR IGNORE INTO {entityTable}(name) VALUES($name); SELECT id FROM {entityTable} WHERE name=$name";
                add.Parameters.AddWithValue("$name", name);
                entityId = Convert.ToInt64(await add.ExecuteScalarAsync(token).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            await using var link = connection.CreateCommand();
            link.Transaction = transaction;
            link.CommandText = $"INSERT OR IGNORE INTO {joinTable}(episode_id,{idColumn}) VALUES($episode,$id)";
            link.Parameters.AddWithValue("$episode", episodeId);
            link.Parameters.AddWithValue("$id", entityId);
            await link.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task ResolveConflictsSupersededByAuthoritativeAuditAsync(SqliteConnection connection,
        SqliteTransaction transaction, long researchId, string now, CancellationToken token)
    {
        await using (var resolve = connection.CreateCommand())
        {
            resolve.Transaction = transaction;
            resolve.CommandText = """
                UPDATE research_conflicts
                SET resolution='ignored',resolved_at=$now
                WHERE research_broadcast_id=$research AND resolution='unresolved'
                """;
            resolve.Parameters.AddWithValue("$research", researchId);
            resolve.Parameters.AddWithValue("$now", now);
            await resolve.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }

        await using var refresh = connection.CreateCommand();
        refresh.Transaction = transaction;
        refresh.CommandText = """
            UPDATE research_broadcasts SET
                needs_review=CASE
                    WHEN EXISTS(SELECT 1 FROM research_reconciliation_candidates rrc
                                WHERE rrc.research_broadcast_id=$research
                                  AND rrc.status='pending' AND rrc.requires_review=1) THEN 1
                    ELSE 0 END,
                research_state=CASE
                    WHEN EXISTS(SELECT 1 FROM research_reconciliation_candidates rrc
                                WHERE rrc.research_broadcast_id=$research
                                  AND rrc.status='pending' AND rrc.requires_review=1) THEN 'conflicting_information'
                    WHEN episode_id IS NOT NULL THEN 'in_library'
                    ELSE 'missing_recording' END,
                updated_at=$now
            WHERE id=$research
            """;
        refresh.Parameters.AddWithValue("$research", researchId);
        refresh.Parameters.AddWithValue("$now", now);
        await refresh.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task EnrichRelatedAsync(SqliteConnection connection, long researchId, TrvPackBroadcast item, CancellationToken token)
    {
        item.Research ??= new TrvPackResearch();
        item.Research.People ??= new TrvPackPeople();
        item.Research.Topics ??= new List<string>();
        item.Research.Moments ??= new List<TrvPackMoment>();
        item.Sources ??= new List<TrvPackSource>();
        await using (var people = connection.CreateCommand())
        {
            people.CommandText = "SELECT name,role FROM research_people WHERE research_broadcast_id=$id ORDER BY role,name";
            people.Parameters.AddWithValue("$id", researchId);
            await using var reader = await people.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                switch (reader.GetString(1))
                {
                    case "host": AddUnique(item.Research.People.Hosts, name); break;
                    case "guest": AddUnique(item.Research.People.Guests, name); break;
                    case "caller": AddUnique(item.Research.People.Callers, name); break;
                    case "mentioned": AddUnique(item.Research.People.MentionedPeople, name); break;
                }
            }
        }
        await using (var topics = connection.CreateCommand())
        {
            topics.CommandText = "SELECT topic FROM research_topics WHERE research_broadcast_id=$id ORDER BY topic";
            topics.Parameters.AddWithValue("$id", researchId);
            await using var reader = await topics.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) AddUnique(item.Research.Topics, reader.GetString(0));
        }
        await using (var sources = connection.CreateCommand())
        {
            sources.CommandText = "SELECT url,title,publisher,accessed_at,supports,notes FROM research_sources WHERE research_broadcast_id=$id ORDER BY confidence DESC,id";
            sources.Parameters.AddWithValue("$id", researchId);
            await using var reader = await sources.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var url = reader.GetString(0);
                if (item.Sources.Any(x => string.Equals(x.Url, url, StringComparison.OrdinalIgnoreCase))) continue;
                item.Sources.Add(new TrvPackSource
                {
                    Url = url,
                    Title = reader.GetString(1),
                    Publisher = reader.GetString(2),
                    Accessed = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                    Supports = reader.GetString(4).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                    Notes = reader.GetString(5)
                });
            }
        }
        await using (var moments = connection.CreateCommand())
        {
            moments.CommandText = "SELECT timestamp_seconds,title,description,tags FROM research_moments WHERE research_broadcast_id=$id ORDER BY timestamp_seconds";
            moments.Parameters.AddWithValue("$id", researchId);
            await using var reader = await moments.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var time = reader.GetInt64(0);
                var title = reader.GetString(1);
                if (item.Research.Moments.Any(x => x.TimestampSeconds == time && string.Equals(x.Title, title, StringComparison.OrdinalIgnoreCase))) continue;
                item.Research.Moments.Add(new TrvPackMoment
                {
                    TimestampSeconds = time,
                    Title = title,
                    Description = reader.GetString(2),
                    Tags = reader.GetString(3).Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                });
            }
        }
    }


    private void CreateResearchImportBackup()
    {
        if (!File.Exists(_database.DatabasePath)) return;
        var directory = Path.GetDirectoryName(_database.DatabasePath) ?? ".";
        var stem = Path.GetFileNameWithoutExtension(_database.DatabasePath);
        var backupPath = Path.Combine(directory, $"{stem}.pre-research-import-{DateTime.UtcNow:yyyyMMdd-HHmmssfff}.sqlite");
        using var source = new SqliteConnection($"Data Source={_database.DatabasePath}");
        using var destination = new SqliteConnection($"Data Source={backupPath}");
        source.Open();
        destination.Open();
        source.BackupDatabase(destination);
    }

    private static TrvPackBroadcast DeserializeBroadcastSafely(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new TrvPackBroadcast();
        try { return KnowledgePackService.DeserializeBroadcast(json) ?? new TrvPackBroadcast(); }
        catch { return new TrvPackBroadcast(); }
    }

    private static string BuildIdentityKey(int collectionId, TrvPackBroadcast item)
    {
        var raw = !string.IsNullOrWhiteSpace(item.BroadcastId)
            ? $"{collectionId}|uid|{NormalizeKey(item.BroadcastId)}"
            : !string.IsNullOrWhiteSpace(item.BroadcastDate)
                ? $"{collectionId}|date|{item.BroadcastDate.Trim()}|{NormalizeKey(EffectiveSlot(item))}|{Math.Max(1, item.PartNumber)}|primary"
                : $"{collectionId}|undated|{NormalizeKey(EffectiveSlot(item))}|{Math.Max(1, item.PartNumber)}|{NormalizeKey(item.Research?.Headline)}|{NormalizeKey(item.Sources?.FirstOrDefault()?.Url)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static void NormalizeIncomingItem(TrvPackBroadcast item)
    {
        item.Show = KnownShowCatalog.Normalize(item.Show) ?? Clean(item.Show);
        item.Research ??= new TrvPackResearch();
        item.Research.Broadcast ??= new TrvPackBroadcastMetadata();
        item.Research.People ??= new TrvPackPeople();
        item.Research.People.Hosts ??= new List<string>();
        item.Research.People.Guests ??= new List<string>();
        item.Research.People.Callers ??= new List<string>();
        item.Research.People.MentionedPeople ??= new List<string>();
        item.Research.Quality ??= new TrvPackResearchQuality();
        item.Research.Catalogue ??= new TrvPackCatalogueMetadata();
        if (KnownShowCatalog.SupportsDateReview(item.Show))
        {
            var isCatalogueShow = KnownShowCatalog.SupportsUndatedCatalogueItems(item.Show);
            if (isCatalogueShow)
                item.Research.Catalogue.Series = FirstNonEmpty(item.Research.Catalogue.Series, item.Show);

            var dateHint = CatalogueDateService.Resolve(
                item.Research.Catalogue.DateReviewDate,
                item.BroadcastDate,
                item.Research.Catalogue.OriginalReleaseDate,
                item.Research.Catalogue.RecordingDate,
                item.Research.Catalogue.OriginalFilename,
                item.Research.Headline);
            var decision = Clean(item.Research.Catalogue.DateReviewStatus).ToLowerInvariant();
            var knownDecision = decision is "pending" or "reopened" or "approved_library_date"
                or "kept_existing" or "ignored"
                or "recording_date_only" or "release_date_only" or "left_undated";
            var hasExplicitReviewEvidence = !string.IsNullOrWhiteSpace(item.Research.Catalogue.DateReviewDate)
                || !string.IsNullOrWhiteSpace(item.Research.Catalogue.DateReviewBasis)
                || !string.IsNullOrWhiteSpace(item.Research.Catalogue.DateReviewNotes);

            if (!string.IsNullOrWhiteSpace(decision)
                && (!knownDecision || (decision == "approved_library_date" && !dateHint.ExactDate.HasValue)))
                decision = "pending";
            else if (string.IsNullOrWhiteSpace(decision)
                && ((isCatalogueShow && dateHint.HasValue) || hasExplicitReviewEvidence))
                decision = "pending";

            if (!string.IsNullOrWhiteSpace(decision))
                item.Research.Catalogue.DateReviewStatus = decision;

            if (dateHint.HasValue
                && string.IsNullOrWhiteSpace(item.Research.Catalogue.DateReviewDate)
                && decision is ("pending" or "reopened"))
            {
                item.Research.Catalogue.DateReviewDate = dateHint.ExactDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    ?? dateHint.DisplayText;
            }
            if (dateHint.HasValue && decision is ("pending" or "reopened"))
            {
                item.Research.Catalogue.DateReviewBasis = FirstNonEmpty(
                    item.Research.Catalogue.DateReviewBasis,
                    item.Research.Quality?.ConfidenceReason,
                    isCatalogueShow
                        ? "Catalogue date supplied by research; confirm whether it belongs in the Library."
                        : "Research date supplied for this broadcast; confirm whether it should replace or organise the Library date.");
            }
            if (dateHint.HasValue && decision == "recording_date_only")
                item.Research.Catalogue.RecordingDate = FirstNonEmpty(
                    item.Research.Catalogue.RecordingDate,
                    item.Research.Catalogue.DateReviewDate,
                    dateHint.DisplayText);
            if (dateHint.HasValue && decision == "release_date_only")
                item.Research.Catalogue.OriginalReleaseDate = FirstNonEmpty(
                    item.Research.Catalogue.OriginalReleaseDate,
                    item.Research.Catalogue.DateReviewDate,
                    dateHint.DisplayText);
            if (isCatalogueShow
                && string.IsNullOrWhiteSpace(item.Research.Catalogue.OriginalReleaseDate)
                && string.IsNullOrWhiteSpace(item.Research.Catalogue.RecordingDate)
                && string.IsNullOrWhiteSpace(item.BroadcastDate)
                && dateHint.HasValue)
                item.Research.Catalogue.OriginalReleaseDate = dateHint.DisplayText;
        }
        item.Research.Broadcast.Station = FirstNonEmpty(item.Research.Broadcast.Station, item.Research.Catalogue.Network);
        item.Research.Broadcast.EpisodeType = FirstNonEmpty(item.Research.Broadcast.EpisodeType, item.Research.Catalogue.Format);
        item.Research.Edition = FirstNonEmpty(item.Research.Edition, item.Research.Catalogue.Programme);
        item.Research.Guests ??= new List<string>();
        item.Research.Topics ??= new List<string>();
        item.Research.Moments ??= new List<TrvPackMoment>();
        item.Sources ??= new List<TrvPackSource>();
        item.ImportPolicy ??= new TrvPackImportPolicy();
    }

    private static string EffectiveSlot(TrvPackBroadcast item) => FirstNonEmpty(item.Slot, item.Research?.Broadcast?.Slot);
    private static string NormalizeKey(string? value) => Clean(value).ToLowerInvariant();
    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
    private static bool IsUncertainDateConfidence(string? value)
    {
        var confidence = Clean(value);
        if (string.IsNullOrWhiteSpace(confidence)) return true;
        if (IsResearchAdoptedDateConfidence(confidence)) return false;
        return !confidence.Equals("High", StringComparison.OrdinalIgnoreCase)
            && !confidence.Equals("Confirmed", StringComparison.OrdinalIgnoreCase)
            && !confidence.Equals("Manual", StringComparison.OrdinalIgnoreCase);
    }

    private static bool DateHintConflictsWithCurrentDate(CatalogueDateHint hint, DateOnly? currentDate)
    {
        if (!hint.HasValue || !currentDate.HasValue) return false;
        if (hint.ExactDate.HasValue) return hint.ExactDate.Value != currentDate.Value;

        if (hint.Precision == CatalogueDatePrecision.Year
            && int.TryParse(hint.DisplayText, NumberStyles.None, CultureInfo.InvariantCulture, out var year))
            return currentDate.Value.Year != year;

        if (hint.Precision == CatalogueDatePrecision.Month
            && DateTime.TryParseExact(hint.DisplayText, "MMMM yyyy", CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces, out var month))
            return currentDate.Value.Year != month.Year || currentDate.Value.Month != month.Month;

        return false;
    }

    private static bool IsResearchAdoptedDateConfidence(string? value)
    {
        var confidence = Clean(value);
        return confidence.StartsWith("Research exact date", StringComparison.OrdinalIgnoreCase)
            || confidence.StartsWith("Research authoritative", StringComparison.OrdinalIgnoreCase)
            || confidence.StartsWith("Research manual", StringComparison.OrdinalIgnoreCase)
            || confidence.StartsWith("Research date approved", StringComparison.OrdinalIgnoreCase);
    }
    private static string FirstNonEmpty(string? first, string? second) => !string.IsNullOrWhiteSpace(first) ? first.Trim() : Clean(second);
    private static string FirstNonEmpty(string? first, string? second, string? third) =>
        FirstNonEmpty(FirstNonEmpty(first, second), third);
    private static IEnumerable<string> Normalize(IEnumerable<string>? values) => (values ?? Array.Empty<string>())
        .Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase);
    private static void AddUnique(ICollection<string> values, string value)
    {
        if (!values.Contains(value, StringComparer.OrdinalIgnoreCase)) values.Add(value);
    }
    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(value.Select(ch => invalid.Contains(ch) ? '-' : ch).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(result) ? "RadioVault-Research" : result;
    }

    private sealed record PendingImport(string Path, string Hash, TrvKnowledgePack Pack);
    private sealed record ExistingResearch(long Id, bool UserModified, string Headline, string Summary);
    private sealed record ExportRow(long ResearchId, bool IsMissing, string BroadcastId, string AirDate, string Slot,
        int PartNumber, int? TotalParts, string Headline, string Summary, string Station, string Edition,
        string Variant, string Era, string EpisodeType, string ArchiveNotes, int Confidence,
        string ConfidenceReason, string ResearchJson, string OriginalFilename);
}
