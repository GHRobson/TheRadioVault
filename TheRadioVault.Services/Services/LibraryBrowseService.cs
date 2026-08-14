using System.Globalization;
using TheRadioVault.Core.Services;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

/// <summary>
/// Canonical library read service for new presentation shells. The verified
/// Library Truth projection is preferred; a legacy read-only fallback keeps a
/// new or not-yet-adopted database usable without inventing canonical state.
/// </summary>
public sealed class LibraryBrowseService : ILibraryBrowseService
{
    private const int LegacyBatchSize = 5000;
    private readonly SqliteDatabase _database;
    private readonly CanonicalLibraryQueryService _canonical;
    private readonly ArchiveService _legacy;

    public LibraryBrowseService(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _canonical = new CanonicalLibraryQueryService(database);
        _legacy = new ArchiveService(database);
    }

    public async Task<LibraryOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var broadcasts = snapshot.Broadcasts;
        var collections = await Task.Run(
            () => LoadCollectionSummaries(broadcasts),
            cancellationToken).ConfigureAwait(false);
        var continueListening = broadcasts
            .Where(x => x.InProgress && !x.Completed)
            .OrderByDescending(x => x.LastPlayedAt ?? DateTimeOffset.MinValue)
            .ThenByDescending(x => x.AirDate)
            .Take(5)
            .ToArray();
        var recent = broadcasts
            .OrderByDescending(x => x.DateAdded)
            .ThenByDescending(x => x.RepresentativeEpisodeId)
            .Take(5)
            .ToArray();
        var today = DateOnly.FromDateTime(DateTime.Today);
        var onThisDay = broadcasts
            .Where(x => x.AirDate.HasValue && x.AirDate.Value.Month == today.Month && x.AirDate.Value.Day == today.Day)
            .OrderByDescending(x => x.AirDate)
            .ThenBy(x => x.CollectionName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        return new LibraryOverview(
            broadcasts.Count,
            broadcasts.Count(x => x.Completed),
            broadcasts.Count(x => x.InProgress && !x.Completed),
            broadcasts.Count(x => x.Favourite),
            broadcasts.Count(x => x.NeedsAttention),
            snapshot.UsesCanonicalLibrary,
            collections,
            continueListening,
            recent,
            onThisDay);
    }

    public async Task<LibraryBroadcastSummary?> GetBroadcastAsync(
        long representativeEpisodeId,
        CancellationToken cancellationToken = default)
    {
        if (representativeEpisodeId <= 0) return null;
        var snapshot = await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Broadcasts.FirstOrDefault(x => x.RepresentativeEpisodeId == representativeEpisodeId);
    }

    public async Task<IReadOnlyList<LibraryBroadcastSummary>> GetBroadcastsAsync(
        IReadOnlyList<long> episodeIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(episodeIds);
        if (episodeIds.Count == 0) return [];
        var snapshot = await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var byId = IndexByRepresentativeEpisodeId(snapshot.Broadcasts);
        var result = new List<LibraryBroadcastSummary>(episodeIds.Count);
        foreach (var requestedId in episodeIds)
        {
            if (byId.TryGetValue(requestedId, out var direct))
            {
                result.Add(direct);
                continue;
            }
            var resolution = snapshot.UsesCanonicalLibrary ? _canonical.ResolveEpisode(requestedId) : null;
            if (resolution is not null && byId.TryGetValue(resolution.RepresentativeEpisodeId, out var canonical))
                result.Add(canonical);
        }
        return result;
    }

    internal async Task<IReadOnlyList<LibraryBroadcastSummary>> GetBroadcastsByIdentityAsync(
        IReadOnlyList<(long EpisodeId, string CanonicalKey)> identities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identities);
        if (identities.Count == 0) return [];
        var snapshot = await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var byIdentity = IndexByCanonicalIdentity(snapshot.Broadcasts);
        var byId = IndexByRepresentativeEpisodeId(snapshot.Broadcasts);
        var result = new List<LibraryBroadcastSummary>(identities.Count);
        foreach (var identity in identities)
        {
            if (byIdentity.TryGetValue(identity, out var exact))
                result.Add(exact);
            else if (byId.TryGetValue(identity.EpisodeId, out var fallback))
                result.Add(fallback);
        }
        return result;
    }

    internal static IReadOnlyDictionary<(long EpisodeId, string CanonicalKey), LibraryBroadcastSummary> IndexByCanonicalIdentity(
        IEnumerable<LibraryBroadcastSummary> broadcasts)
        => broadcasts
            .Where(value => value.RepresentativeEpisodeId > 0 && !string.IsNullOrWhiteSpace(value.CanonicalKey))
            .GroupBy(value => (value.RepresentativeEpisodeId, value.CanonicalKey))
            .ToDictionary(group => group.Key, SelectPreferredDuplicate);

    internal static IReadOnlyDictionary<long, LibraryBroadcastSummary> IndexByRepresentativeEpisodeId(
        IEnumerable<LibraryBroadcastSummary> broadcasts)
        => broadcasts
            .Where(value => value.RepresentativeEpisodeId > 0)
            .GroupBy(value => value.RepresentativeEpisodeId)
            .ToDictionary(group => group.Key, SelectPreferredDuplicate);

    private static LibraryBroadcastSummary SelectPreferredDuplicate(IEnumerable<LibraryBroadcastSummary> candidates)
        => candidates
            .OrderBy(value => value.NeedsAttention)
            .ThenByDescending(value => value.PhysicalFileCount)
            .ThenByDescending(value => value.DurationMs)
            .ThenBy(value => value.CanonicalKey, StringComparer.Ordinal)
            .First();

    public async Task<LibraryBrowseResult> BrowseAsync(
        LibraryBrowseRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var snapshot = await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<LibraryBroadcastSummary> query = snapshot.Broadcasts;

        if (request.CollectionId.HasValue)
        {
            var collectionName = ResolveCollectionName(request.CollectionId.Value);
            query = query.Where(x => CollectionIdentityResolver.Matches(x.CollectionName, collectionName));
        }
        if (request.Year.HasValue)
            query = query.Where(x => x.AirDate?.Year == request.Year.Value);
        if (request.Month.HasValue)
            query = query.Where(x => x.AirDate?.Month == request.Month.Value);
        if (request.HideCompleted)
            query = query.Where(x => !x.Completed);

        var today = DateOnly.FromDateTime(DateTime.Today);
        query = request.Filter switch
        {
            LibraryListeningFilter.ContinueListening => query.Where(x => x.InProgress && !x.Completed),
            LibraryListeningFilter.Favourites => query.Where(x => x.Favourite),
            LibraryListeningFilter.Completed => query.Where(x => x.Completed),
            LibraryListeningFilter.Unplayed => query.Where(x => !x.Completed && !x.InProgress),
            LibraryListeningFilter.NeedsAttention => query.Where(x => x.NeedsAttention),
            LibraryListeningFilter.RecentlyAdded => query.OrderByDescending(x => x.DateAdded),
            LibraryListeningFilter.OnThisDay => query.Where(x => x.AirDate.HasValue && x.AirDate.Value.Month == today.Month && x.AirDate.Value.Day == today.Day),
            _ => query
        };

        if (request.HasTranscript)
        {
            var transcriptEpisodes = LoadTranscriptEpisodeIds();
            query = query.Where(x => transcriptEpisodes.Contains(x.RepresentativeEpisodeId));
        }

        if (!string.IsNullOrWhiteSpace(request.SearchText))
        {
            var search = request.SearchText.Trim();
            var extended = LoadExtendedSearchMatches(search);
            query = query
                .Select(item => ApplySearchMatch(item, search, request.SearchScope, extended))
                .Where(item => item.SearchScore > 0);
        }

        query = !string.IsNullOrWhiteSpace(request.SearchText)
            ? query.OrderByDescending(x => x.SearchScore)
                .ThenByDescending(x => x.AirDate ?? DateOnly.MinValue)
                .ThenByDescending(x => x.RepresentativeEpisodeId)
            : request.Filter == LibraryListeningFilter.RecentlyAdded
            ? query.OrderByDescending(x => x.DateAdded).ThenByDescending(x => x.RepresentativeEpisodeId)
            : request.NewestFirst
                ? query.OrderByDescending(x => x.AirDate ?? DateOnly.MinValue)
                    .ThenByDescending(x => x.RepresentativeEpisodeId)
                : query.OrderBy(x => x.AirDate ?? DateOnly.MaxValue)
                    .ThenBy(x => x.RepresentativeEpisodeId);

        var materialized = query.ToArray();
        var page = materialized
            .Skip(Math.Max(0, request.Offset))
            .Take(Math.Clamp(request.Limit, 1, 10000))
            .ToArray();
        return new LibraryBrowseResult(page, materialized.Length, snapshot.UsesCanonicalLibrary);
    }

    public async Task<LibrarySearchFacets> GetSearchFacetsAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var years = snapshot.Broadcasts
            .Where(x => x.AirDate.HasValue)
            .Select(x => x.AirDate!.Value.Year)
            .Distinct()
            .OrderByDescending(x => x)
            .ToArray();
        return new LibrarySearchFacets(years, LoadTranscriptEpisodeIds().Count);
    }

    public Task<IReadOnlyList<LibrarySearchSuggestion>> GetSearchSuggestionsAsync(
        string prefix,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Trim().Length < 2)
            return Task.FromResult<IReadOnlyList<LibrarySearchSuggestion>>(Array.Empty<LibrarySearchSuggestion>());
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT value,kind,SUM(match_count) AS matches FROM (
                SELECT c.name AS value,'Show' AS kind,COUNT(*) AS match_count
                  FROM collections c JOIN episodes e ON e.collection_id=c.id
                 WHERE COALESCE(e.hidden,0)=0 AND c.name LIKE $prefix COLLATE NOCASE GROUP BY c.name
                UNION ALL
                SELECT e.title,'Broadcast',COUNT(*) FROM episodes e
                 WHERE COALESCE(e.hidden,0)=0 AND e.title<>'' AND e.title LIKE $prefix COLLATE NOCASE GROUP BY e.title
                UNION ALL
                SELECT g.name,'Person',COUNT(*) FROM guests g JOIN episode_guests eg ON eg.guest_id=g.id
                 WHERE g.name LIKE $prefix COLLATE NOCASE GROUP BY g.name
                UNION ALL
                SELECT rp.name,'Person',COUNT(*) FROM research_people rp
                 WHERE rp.name LIKE $prefix COLLATE NOCASE GROUP BY rp.name
                UNION ALL
                SELECT rt.topic,'Topic',COUNT(*) FROM research_topics rt
                 WHERE rt.topic LIKE $prefix COLLATE NOCASE GROUP BY rt.topic
            ) suggestions
            WHERE value<>''
            GROUP BY value,kind
            ORDER BY CASE kind WHEN 'Show' THEN 0 WHEN 'Person' THEN 1 WHEN 'Topic' THEN 2 ELSE 3 END,
                     matches DESC,value COLLATE NOCASE
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$prefix", prefix.Trim() + "%");
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 25));
        var result = new List<LibrarySearchSuggestion>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            result.Add(new LibrarySearchSuggestion(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return Task.FromResult<IReadOnlyList<LibrarySearchSuggestion>>(result);
    }

    public async Task<IReadOnlyList<LibraryArchivePeriodSummary>> GetArchivePeriodsAsync(
        int? collectionId,
        int? year,
        bool hideCompleted = false,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await LoadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        IEnumerable<LibraryBroadcastSummary> query = snapshot.Broadcasts.Where(x => x.AirDate.HasValue);
        if (collectionId.HasValue)
        {
            var collectionName = ResolveCollectionName(collectionId.Value);
            query = query.Where(x => CollectionIdentityResolver.Matches(x.CollectionName, collectionName));
        }
        if (year.HasValue)
            query = query.Where(x => x.AirDate!.Value.Year == year.Value);
        if (hideCompleted)
            query = query.Where(x => !x.Completed);

        var groups = year.HasValue
            ? query.GroupBy(x => x.AirDate!.Value.Month).OrderBy(x => x.Key)
            : query.GroupBy(x => x.AirDate!.Value.Year).OrderByDescending(x => x.Key);

        return groups.Select(group => CreateArchivePeriod(
            group.Key,
            year.HasValue ? CultureInfo.CurrentCulture.DateTimeFormat.GetMonthName(group.Key) : group.Key.ToString(CultureInfo.InvariantCulture),
            group.ToArray())).ToArray();
    }

    private static LibraryArchivePeriodSummary CreateArchivePeriod(
        int value,
        string title,
        IReadOnlyList<LibraryBroadcastSummary> broadcasts)
    {
        var completed = broadcasts.Count(x => x.Completed);
        var percent = broadcasts.Count == 0 ? 0 : (int)Math.Round(completed * 100d / broadcasts.Count);
        var favourites = broadcasts.Count(x => x.Favourite);
        var shows = broadcasts
            .Select(x => x.CollectionName)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var showsText = shows.Length switch
        {
            0 => "Archive",
            1 => shows[0],
            2 => string.Join("  ·  ", shows),
            _ => $"{string.Join("  ·  ", shows.Take(2))}  +{shows.Length - 2} more"
        };
        return new LibraryArchivePeriodSummary(
            value,
            title,
            broadcasts.Count,
            completed,
            favourites,
            percent,
            $"{completed:N0} listened · {percent}%",
            showsText,
            broadcasts.Select(x => x.ArtworkPath).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)));
    }

    private IReadOnlyList<LibraryCollectionSummary> LoadCollectionSummaries(
        IReadOnlyList<LibraryBroadcastSummary> broadcasts)
    {
        using var connection = _database.OpenConnection();
        var families = CollectionIdentityResolver.LoadFamilies(connection);
        var countsByShow = broadcasts
            .GroupBy(item => CollectionIdentityResolver.Canonicalize(item.CollectionName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var result = new List<LibraryCollectionSummary>();
        var includedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // First-class shows keep their stable ordering, but only appear when
        // the canonical Library actually contains content for that logical
        // show. Counts are based on canonical names rather than raw numeric
        // collection IDs so older alias rows cannot hide valid broadcasts.
        foreach (var show in KnownShowCatalog.Collections.Where(definition =>
                     !definition.CanonicalName.Equals(KnownShowCatalog.Unsorted, StringComparison.OrdinalIgnoreCase)))
        {
            if (!countsByShow.TryGetValue(show.CanonicalName, out var broadcastCount) || broadcastCount <= 0)
                continue;
            var family = families.FirstOrDefault(candidate =>
                candidate.CanonicalName.Equals(show.CanonicalName, StringComparison.OrdinalIgnoreCase));
            if (family is null) continue;
            result.Add(new LibraryCollectionSummary(
                family.PreferredCollectionId,
                show.CanonicalName,
                broadcastCount));
            includedNames.Add(show.CanonicalName);
        }

        // Preserve custom collections and Unsorted only when they contain
        // content. Alias rows belonging to a first-class show are already
        // represented by the canonical section above.
        foreach (var family in families)
        {
            if (includedNames.Contains(family.CanonicalName)) continue;
            if (!countsByShow.TryGetValue(family.CanonicalName, out var count) || count <= 0) continue;
            result.Add(new LibraryCollectionSummary(
                family.PreferredCollectionId,
                family.CanonicalName,
                count));
        }

        return result;
    }

    private string ResolveCollectionName(int collectionId)
    {
        using var connection = _database.OpenConnection();
        return CollectionIdentityResolver.ResolveFamily(connection, collectionId)?.CanonicalName
            ?? collectionId.ToString(CultureInfo.InvariantCulture);
    }

    private async Task<LibrarySnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var canonicalSummary = await Task.Run(_canonical.GetSummary, cancellationToken).ConfigureAwait(false);
        if (canonicalSummary.IsCutoverReady)
        {
            var canonical = await Task.Run(_canonical.GetBroadcasts, cancellationToken).ConfigureAwait(false);
            var playable = IndexByRepresentativeEpisodeId(canonical.Select(MapCanonical))
                .Values
                .ToArray();
            return new LibrarySnapshot(playable, true);
        }

        var legacy = new List<LibraryBroadcastSummary>();
        var offset = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await _legacy.SearchAsync(
                new ArchiveSearchRequest(Limit: LegacyBatchSize, Offset: offset, NewestFirst: true),
                cancellationToken).ConfigureAwait(false);
            legacy.AddRange(batch.Select(MapLegacy));
            if (batch.Count < LegacyBatchSize) break;
            offset += batch.Count;
        }
        return new LibrarySnapshot(legacy, false);
    }

    private static LibraryBroadcastSummary MapCanonical(CanonicalLibraryEntry item)
    {
        var completed = string.Equals(item.ListeningStatus, "Completed", StringComparison.OrdinalIgnoreCase);
        var position = item.DurationMs > 0
            ? Math.Clamp(item.PositionMs, 0, item.DurationMs)
            : Math.Max(0, item.PositionMs);
        return new LibraryBroadcastSummary(
            item.CanonicalKey,
            item.RepresentativeEpisodeId,
            item.BroadcastUid,
            item.CollectionId,
            item.CollectionName,
            item.AirDate,
            item.DateAdded,
            item.BroadcastSlot,
            item.Headline,
            item.Description,
            item.Favourite,
            completed,
            position > 0 && !completed,
            position,
            Math.Max(0, item.DurationMs),
            item.LastPlayedAt,
            item.ArtworkPath,
            Math.Max(1, item.RecordingCount),
            Math.Max(1, item.SegmentCount),
            Math.Max(1, item.PhysicalFileCount),
            item.NeedsAttention,
            item.AttentionReason);
    }

    private static LibraryBroadcastSummary MapLegacy(BroadcastSummary item)
    {
        var duration = Math.Max(0, item.DurationMs);
        var position = duration > 0 ? Math.Clamp(item.PositionMs, 0, duration) : Math.Max(0, item.PositionMs);
        return new LibraryBroadcastSummary(
            $"LEGACY-{item.Id}",
            item.Id,
            item.BroadcastId,
            item.CollectionId,
            item.CollectionName,
            item.AirDate,
            DateTimeOffset.MinValue.AddTicks(Math.Max(0, item.Id)),
            string.Empty,
            item.Title,
            item.Description,
            item.Favourite,
            item.Completed,
            position > 0 && !item.Completed,
            position,
            duration,
            item.LastPlayedAt,
            item.ArtworkPath,
            1,
            1,
            1,
            false,
            string.Empty);
    }

    private static bool Contains(string? value, string search) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(search, StringComparison.CurrentCultureIgnoreCase);

    private LibraryBroadcastSummary ApplySearchMatch(
        LibraryBroadcastSummary item,
        string search,
        LibrarySearchScope scope,
        IReadOnlyDictionary<long, ExtendedSearchMatch> extended)
    {
        var score = 0;
        var context = string.Empty;
        long? searchStartMs = null;
        if (scope is LibrarySearchScope.All or LibrarySearchScope.TitlesAndSummaries)
        {
            if (string.Equals(item.Title?.Trim(), search, StringComparison.CurrentCultureIgnoreCase)) score = 120;
            else if (Contains(item.Title, search)) score = 100;
            else if (Contains(item.Description, search)) { score = 75; context = BuildExcerpt(item.Description!, search, "Summary"); }
            else if (scope == LibrarySearchScope.All && Contains(item.CollectionName, search)) score = 90;
            else if (scope == LibrarySearchScope.All && (Contains(item.BroadcastId, search) || Contains(item.BroadcastSlot, search))) score = 70;
            else if (scope == LibrarySearchScope.All && item.AirDate?.ToString("yyyy-MM-dd").Contains(search, StringComparison.OrdinalIgnoreCase) == true) score = 65;
        }

        if (extended.TryGetValue(item.RepresentativeEpisodeId, out var detail))
        {
            var candidate = detail.ForScope(scope);
            if (candidate is not null && candidate.Score > score)
            {
                score = candidate.Score;
                context = candidate.Context;
                searchStartMs = candidate.StartMs;
            }
        }
        return item with { SearchScore = score, SearchContext = context, SearchStartMs = searchStartMs };
    }

    private IReadOnlyDictionary<long, ExtendedSearchMatch> LoadExtendedSearchMatches(string search)
    {
        var result = new Dictionary<long, ExtendedSearchMatch>();
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT e.id,COALESCE(e.hosts,''),COALESCE(e.callers,''),
                   COALESCE(e.mentioned_people,''),COALESCE(e.archive_notes,''),
                   COALESCE((SELECT group_concat(g.name,'; ') FROM episode_guests eg JOIN guests g ON g.id=eg.guest_id WHERE eg.episode_id=e.id),''),
                   COALESCE((SELECT group_concat(rp.name,'; ') FROM research_people rp JOIN research_broadcasts rb ON rb.id=rp.research_broadcast_id WHERE rb.episode_id=e.id),''),
                   COALESCE((SELECT group_concat(rt.topic,'; ') FROM research_topics rt JOIN research_broadcasts rb ON rb.id=rt.research_broadcast_id WHERE rb.episode_id=e.id),''),
                   COALESCE((SELECT group_concat(rb.research_json,' ') FROM research_broadcasts rb WHERE rb.episode_id=e.id),''),
                   COALESCE((SELECT t.full_text FROM transcripts t WHERE t.episode_id=e.id LIMIT 1),''),
                   (SELECT ts.start_ms
                      FROM transcripts matching_transcript
                      JOIN transcript_segments ts ON ts.transcript_id=matching_transcript.id
                     WHERE matching_transcript.episode_id=e.id
                       AND instr(lower(ts.text),lower($search))>0
                     ORDER BY ts.start_ms,ts.segment_index
                     LIMIT 1)
              FROM episodes e
             WHERE COALESCE(e.hidden,0)=0
               AND (
                    instr(lower(COALESCE(e.hosts,'')),lower($search))>0
                 OR instr(lower(COALESCE(e.callers,'')),lower($search))>0
                 OR instr(lower(COALESCE(e.mentioned_people,'')),lower($search))>0
                 OR instr(lower(COALESCE(e.archive_notes,'')),lower($search))>0
                 OR EXISTS(SELECT 1 FROM episode_guests eg JOIN guests g ON g.id=eg.guest_id WHERE eg.episode_id=e.id AND instr(lower(g.name),lower($search))>0)
                 OR EXISTS(SELECT 1 FROM research_people rp JOIN research_broadcasts rb ON rb.id=rp.research_broadcast_id WHERE rb.episode_id=e.id AND instr(lower(rp.name),lower($search))>0)
                 OR EXISTS(SELECT 1 FROM research_topics rt JOIN research_broadcasts rb ON rb.id=rt.research_broadcast_id WHERE rb.episode_id=e.id AND instr(lower(rt.topic),lower($search))>0)
                 OR EXISTS(SELECT 1 FROM research_broadcasts rb WHERE rb.episode_id=e.id AND instr(lower(rb.research_json),lower($search))>0)
                 OR EXISTS(SELECT 1 FROM transcripts t WHERE t.episode_id=e.id AND instr(lower(t.full_text),lower($search))>0)
               );
            """;
        command.Parameters.AddWithValue("$search", search);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var people = string.Join("; ", Enumerable.Range(1, 3).Select(reader.GetString).Append(reader.GetString(5)).Append(reader.GetString(6)));
            var topics = reader.GetString(7);
            var notes = string.Join(" ", reader.GetString(4), reader.GetString(8));
            var transcript = reader.GetString(9);
            long? transcriptStartMs = reader.IsDBNull(10) ? null : reader.GetInt64(10);
            result[id] = new ExtendedSearchMatch(
                Contains(people, search) ? new SearchMatch(88, BuildExcerpt(people, search, "Person"), null) : null,
                Contains(topics, search) ? new SearchMatch(84, BuildExcerpt(topics, search, "Topic"), null) : null,
                Contains(notes, search) ? new SearchMatch(72, BuildExcerpt(notes, search, "Research"), null) : null,
                Contains(transcript, search) ? new SearchMatch(
                    68,
                    BuildExcerpt(transcript, search, transcriptStartMs.HasValue ? $"Transcript · {FormatTime(transcriptStartMs.Value)}" : "Transcript"),
                    transcriptStartMs) : null);
        }
        return result;
    }

    private HashSet<long> LoadTranscriptEpisodeIds()
    {
        using var connection = _database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT episode_id FROM transcripts";
        using var reader = command.ExecuteReader();
        var result = new HashSet<long>();
        while (reader.Read()) result.Add(reader.GetInt64(0));
        return result;
    }

    private static string BuildExcerpt(string value, string search, string label)
    {
        var normalized = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var index = normalized.IndexOf(search, StringComparison.CurrentCultureIgnoreCase);
        if (index < 0) return label;
        var start = Math.Max(0, index - 55);
        var length = Math.Min(normalized.Length - start, Math.Max(100, search.Length + 110));
        var excerpt = normalized.Substring(start, length).Trim();
        if (start > 0) excerpt = "…" + excerpt;
        if (start + length < normalized.Length) excerpt += "…";
        return $"{label}: {excerpt}";
    }

    private static string FormatTime(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }

    private sealed record SearchMatch(int Score, string Context, long? StartMs);

    private sealed record ExtendedSearchMatch(
        SearchMatch? People,
        SearchMatch? Topics,
        SearchMatch? Research,
        SearchMatch? Transcript)
    {
        public SearchMatch? ForScope(LibrarySearchScope scope) => scope switch
        {
            LibrarySearchScope.People => People,
            LibrarySearchScope.Topics => Topics,
            LibrarySearchScope.Research => Research,
            LibrarySearchScope.Transcripts => Transcript,
            LibrarySearchScope.TitlesAndSummaries => null,
            _ => new[] { People, Topics, Research, Transcript }
                .Where(x => x is not null)
                .OrderByDescending(x => x!.Score)
                .FirstOrDefault()
        };
    }

    private sealed record LibrarySnapshot(IReadOnlyList<LibraryBroadcastSummary> Broadcasts, bool UsesCanonicalLibrary);
}
