using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text;
using System.Globalization;
using Microsoft.Data.Sqlite;
using TheRadioVault.Core.Domain;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Services;

public sealed class WikiConcurrencyException : InvalidOperationException
{
    public WikiConcurrencyException(string message) : base(message) { }
}

/// <summary>
/// Authoritative, UI-independent wiki repository. A page revision is the
/// optimistic-concurrency boundary used by native, Web and authoring-pack
/// imports, so an external agent can never silently replace a newer edit.
/// </summary>
public sealed class WikiService : IWikiService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly Regex WikiLinkPattern = new(
        @"\[\[(?<target>[^\]|]+)(?:\|(?<label>[^\]]+))?\]\]|\[(?<label2>[^\]]+)\]\(wiki:(?<target2>[^)]+)\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly SqliteDatabase _database;

    public WikiService(SqliteDatabase database) => _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<WikiOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM wiki_pages),
                (SELECT COUNT(*) FROM wiki_pages WHERE status='Published'),
                (SELECT COUNT(*) FROM wiki_pages WHERE status='Draft'),
                (SELECT COUNT(*) FROM wiki_sources),
                (SELECT COUNT(*) FROM wiki_citations),
                (SELECT COUNT(*) FROM wiki_images),
                (SELECT COUNT(*) FROM wiki_timeline_events),
                (SELECT MAX(updated_at) FROM wiki_pages),
                (SELECT MAX(imported_at) FROM wiki_import_runs);
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        return new WikiOverview(
            reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3),
            reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6),
            ReadDateTime(reader, 7), ReadDateTime(reader, 8));
    }

    public async Task<IReadOnlyList<WikiPageSummary>> BrowseAsync(
        WikiBrowseQuery query,
        CancellationToken cancellationToken = default)
    {
        query ??= new WikiBrowseQuery();
        var search = (query.Search ?? string.Empty).Trim();
        var pageType = (query.PageType ?? string.Empty).Trim();
        var status = (query.Status ?? string.Empty).Trim();
        var limit = Math.Clamp(query.Limit, 1, 5000);
        var results = new List<WikiPageSummary>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.id,p.slug,p.title,p.page_type,p.summary,p.status,p.revision,p.updated_at,
                   (SELECT COUNT(*) FROM wiki_citations c WHERE c.page_id=p.id),
                   (SELECT COUNT(*) FROM wiki_page_images pi WHERE pi.page_id=p.id),
                   (SELECT COUNT(*) FROM wiki_timeline_events e WHERE e.page_id=p.id)
              FROM wiki_pages p
             WHERE ($search='' OR p.title LIKE $like COLLATE NOCASE OR p.slug LIKE $like COLLATE NOCASE
                    OR p.summary LIKE $like COLLATE NOCASE OR p.body_markdown LIKE $like COLLATE NOCASE
                    OR EXISTS(SELECT 1 FROM wiki_page_aliases a WHERE a.page_id=p.id AND a.alias LIKE $like COLLATE NOCASE))
               AND ($type='' OR p.page_type=$type COLLATE NOCASE)
               AND ($status='' OR p.status=$status COLLATE NOCASE)
               AND ($status='Archived' OR p.status<>'Archived')
             ORDER BY CASE p.status WHEN 'Published' THEN 0 WHEN 'Draft' THEN 1 ELSE 2 END,
                      p.title COLLATE NOCASE
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$search", search);
        command.Parameters.AddWithValue("$like", $"%{search}%");
        command.Parameters.AddWithValue("$type", pageType);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$limit", limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            results.Add(new WikiPageSummary(
                ReadGuid(reader, 0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetInt32(6),
                ReadRequiredDateTime(reader, 7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10)));
        }
        return results;
    }

    public async Task<WikiPageDocument?> GetPageAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        if (pageId == Guid.Empty) return null;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var redirect = connection.CreateCommand())
        {
            redirect.CommandText = "SELECT to_page_id FROM wiki_page_redirects WHERE from_page_id=$id";
            redirect.Parameters.AddWithValue("$id", pageId.ToString("D"));
            var target = await redirect.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (target is string text && Guid.TryParse(text, out var redirected)) pageId = redirected;
        }
        WikiPageCore? core;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT id,slug,title,page_type,summary,body_markdown,status,revision,
                       created_at,updated_at,created_by,last_editor
                  FROM wiki_pages WHERE id=$id;
                """;
            command.Parameters.AddWithValue("$id", pageId.ToString("D"));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
            core = new WikiPageCore(
                ReadGuid(reader, 0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7),
                ReadRequiredDateTime(reader, 8), ReadRequiredDateTime(reader, 9), reader.GetString(10), reader.GetString(11));
        }

        var aliases = await ReadAliasesAsync(connection, pageId, cancellationToken).ConfigureAwait(false);
        var relationships = await ReadRelationshipsAsync(connection, pageId, cancellationToken).ConfigureAwait(false);
        var citations = await ReadCitationsAsync(connection, pageId, cancellationToken).ConfigureAwait(false);
        var images = await ReadPageImagesAsync(connection, pageId, cancellationToken).ConfigureAwait(false);
        var timeline = await ReadTimelineAsync(connection, pageId, cancellationToken).ConfigureAwait(false);
        var entityLink = ArchiveEntityLinkFactory.ForWikiPage(core.PageId, core.PageType, core.Title);
        var entityLinks = await ReadEntityLinksAsync(
            connection, core.BodyMarkdown, images, timeline, cancellationToken).ConfigureAwait(false);
        return new WikiPageDocument(
            core.PageId, core.Slug, core.Title, core.PageType, core.Summary, core.BodyMarkdown,
            core.Status, core.Revision, core.CreatedAt, core.UpdatedAt, core.CreatedBy, core.LastEditor,
            aliases, relationships, citations, images, timeline, entityLink, entityLinks);
    }

    private static async Task<IReadOnlyList<ArchiveEntityLink>> ReadEntityLinksAsync(
        SqliteConnection connection,
        string bodyMarkdown,
        IReadOnlyList<WikiPageImageLink> images,
        IReadOnlyList<WikiTimelineEventRecord> timeline,
        CancellationToken cancellationToken)
    {
        var targets = WikiLinkPattern.Matches(bodyMarkdown ?? string.Empty)
            .Select(match => match.Groups["target"].Success
                ? match.Groups["target"].Value
                : match.Groups["target2"].Value)
            .Select(NormalizeLinkKey)
            .Where(target => target.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var pageLinks = new Dictionary<string, ArchiveEntityLink>(StringComparer.Ordinal);
        if (targets.Count > 0)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id,slug,title,page_type FROM wiki_pages WHERE status<>'Archived'";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var link = ArchiveEntityLinkFactory.ForWikiPage(
                    Guid.Parse(reader.GetString(0)), reader.GetString(3), reader.GetString(2)) with
                {
                    Relationship = "inline"
                };
                pageLinks.TryAdd(NormalizeLinkKey(reader.GetString(1)), link);
                pageLinks.TryAdd(NormalizeLinkKey(reader.GetString(2)), link);
            }
        }

        var links = new List<ArchiveEntityLink>();
        foreach (var target in targets)
            if (pageLinks.TryGetValue(target, out var link)) links.Add(link);
        links.AddRange(images.Select(image => ArchiveEntityLinkFactory.ForImage(
            image.ImageId,
            image.Image?.Caption ?? image.Image?.AltText ?? image.Image?.OriginalFileName ?? "Explore image") with
        {
            Relationship = string.IsNullOrWhiteSpace(image.Role) ? "image" : image.Role.Trim().ToLowerInvariant()
        }));
        foreach (var item in timeline)
        {
            links.Add(ArchiveEntityLinkFactory.ForTimeline(item.EventId, item.Title) with { Relationship = "timeline" });
            links.AddRange(item.Broadcasts.Select(broadcast =>
                ArchiveEntityLinkFactory.ForBroadcast(broadcast.EpisodeId, broadcast.Label) with
                {
                    Relationship = "timeline-broadcast"
                }));
        }
        return links.DistinctBy(link => (link.EntityKey, link.Relationship)).ToArray();
    }

    public async Task<WikiImageContent?> GetImageAsync(Guid imageId, CancellationToken cancellationToken = default)
    {
        if (imageId == Guid.Empty) return null;
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT media_type,original_file_name,content FROM wiki_images WHERE id=$id";
        command.Parameters.AddWithValue("$id", imageId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new WikiImageContent(imageId, reader.GetString(0), reader.GetString(1), (byte[])reader[2]);
    }

    public async Task<IReadOnlyList<WikiRevisionRecord>> GetRevisionsAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        if (pageId == Guid.Empty) return Array.Empty<WikiRevisionRecord>();
        var values = new List<WikiRevisionRecord>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT revision,snapshot_json,change_summary,author,import_run_id,created_at
              FROM wiki_page_revisions WHERE page_id=$page ORDER BY revision DESC;
            """;
        command.Parameters.AddWithValue("$page", pageId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var snapshot = JsonSerializer.Deserialize<WikiRevisionSnapshot>(reader.GetString(1), JsonOptions)
                ?? throw new InvalidDataException($"Wiki revision {reader.GetInt32(0)} has an invalid snapshot.");
            values.Add(new WikiRevisionRecord(pageId, reader.GetInt32(0), snapshot.Slug, snapshot.Title, snapshot.PageType,
                snapshot.Summary, snapshot.BodyMarkdown, snapshot.Status, reader.GetString(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4), ReadRequiredDateTime(reader, 5)));
        }
        return values;
    }

    public async Task<WikiPageSaveResult> RestoreRevisionAsync(WikiRevisionRestoreRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var current = await GetPageAsync(request.PageId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The Wiki page no longer exists.");
        if (current.Revision != request.ExpectedCurrentRevision)
            throw new WikiConcurrencyException($"'{current.Title}' changed on another device. Reload revision {current.Revision} before restoring history.");
        var historical = (await GetRevisionsAsync(request.PageId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(x => x.Revision == request.Revision)
            ?? throw new InvalidOperationException($"Revision {request.Revision:N0} was not found.");
        return await SavePageAsync(new WikiPageDraft(current.PageId, historical.Slug, historical.Title,
            historical.PageType, historical.Summary, historical.BodyMarkdown, historical.Status, current.Revision,
            $"Restored content from revision {historical.Revision:N0}",
            string.IsNullOrWhiteSpace(request.Editor) ? "Radio Vault user" : request.Editor,
            current.Aliases, current.Citations, current.Images.Select(x => new WikiImageDraft(x)).ToArray(), current.Timeline),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<WikiCitationAuditReport> AuditCitationsAsync(CancellationToken cancellationToken = default)
    {
        var summaries = await BrowseAsync(new WikiBrowseQuery(Limit: 5000), cancellationToken).ConfigureAwait(false);
        var issues = new List<WikiCitationAuditIssue>();
        var citedPages = 0;
        var totalCitations = 0;
        var sourceIds = new HashSet<Guid>();
        foreach (var summary in summaries)
        {
            var page = await GetPageAsync(summary.PageId, cancellationToken).ConfigureAwait(false);
            if (page is null) continue;
            totalCitations += page.Citations.Count;
            if (page.Citations.Count > 0) citedPages++;
            if (page.BodyMarkdown.Trim().Length >= 250 && page.Citations.Count == 0)
                issues.Add(new WikiCitationAuditIssue(page.PageId, page.Title, "Warning", "article-without-citations", "The article has substantial text but no citations."));
            var expectedOrdinal = 1;
            foreach (var citation in page.Citations.OrderBy(x => x.Ordinal))
            {
                sourceIds.Add(citation.SourceId);
                if (citation.Ordinal != expectedOrdinal)
                    issues.Add(new WikiCitationAuditIssue(page.PageId, page.Title, "Error", "citation-order", $"Citation numbering jumps from {expectedOrdinal:N0} to {citation.Ordinal:N0}."));
                expectedOrdinal = citation.Ordinal + 1;
                if (!page.BodyMarkdown.Contains($"[{citation.Ordinal}]", StringComparison.Ordinal))
                    issues.Add(new WikiCitationAuditIssue(page.PageId, page.Title, "Warning", "unused-citation", $"Citation [{citation.Ordinal}] is not referenced in the article text."));
                var source = citation.Source;
                if (source is null)
                {
                    issues.Add(new WikiCitationAuditIssue(page.PageId, page.Title, "Error", "missing-source", $"Citation [{citation.Ordinal}] has no source record."));
                    continue;
                }
                if (string.Equals(source.SourceType, "Web", StringComparison.OrdinalIgnoreCase) &&
                    string.IsNullOrWhiteSpace(source.Url) && string.IsNullOrWhiteSpace(source.ArchivedUrl))
                    issues.Add(new WikiCitationAuditIssue(page.PageId, page.Title, "Warning", "web-source-without-url", $"Citation [{citation.Ordinal}] is a Web source without a URL."));
                if (string.Equals(source.SourceType, "Broadcast", StringComparison.OrdinalIgnoreCase) &&
                    source.EpisodeId is null && source.MomentId is null && string.IsNullOrWhiteSpace(source.BroadcastUid))
                    issues.Add(new WikiCitationAuditIssue(page.PageId, page.Title, "Error", "broadcast-source-without-archive-link", $"Citation [{citation.Ordinal}] is a Broadcast source that is not linked to the archive."));
                if (source.PublishedDate is null && string.IsNullOrWhiteSpace(source.Locator))
                    issues.Add(new WikiCitationAuditIssue(page.PageId, page.Title, "Warning", "weak-source-locator", $"Citation [{citation.Ordinal}] has neither a publication date nor a locator."));
            }
        }
        return new WikiCitationAuditReport(DateTimeOffset.UtcNow, summaries.Count, citedPages, totalCitations, sourceIds.Count, issues);
    }

    public async Task<WikiNavigationContext> GetNavigationContextAsync(Guid pageId, CancellationToken cancellationToken = default)
    {
        if (pageId == Guid.Empty) return new WikiNavigationContext(Array.Empty<WikiPageSummary>(), Array.Empty<WikiPageSummary>(), Array.Empty<WikiMissingLink>());
        var graph = await LoadWikiGraphAsync(cancellationToken).ConfigureAwait(false);
        if (!graph.Documents.TryGetValue(pageId, out var current))
            return new WikiNavigationContext(Array.Empty<WikiPageSummary>(), Array.Empty<WikiPageSummary>(), Array.Empty<WikiMissingLink>());

        var relatedIds = new HashSet<Guid>(current.Relationships.Select(x => x.ToPageId));
        var missing = new List<WikiMissingLink>();
        foreach (var link in ExtractWikiLinks(current))
        {
            if (ResolveLink(graph.Index, link.Target) is { } linked) relatedIds.Add(linked);
            else missing.Add(new WikiMissingLink(current.PageId, current.Title, link.Target, link.Label));
        }

        foreach (var other in graph.Documents.Values)
            if (other.Relationships.Any(x => x.ToPageId == pageId)) relatedIds.Add(other.PageId);

        var backlinks = graph.Documents.Values
            .Where(x => x.PageId != pageId && (x.Relationships.Any(r => r.ToPageId == pageId) ||
                ExtractWikiLinks(x).Any(link => ResolveLink(graph.Index, link.Target) == pageId)))
            .Select(x => graph.Summaries[x.PageId])
            .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var related = relatedIds.Where(x => x != pageId && graph.Summaries.ContainsKey(x))
            .Select(x => graph.Summaries[x])
            .OrderByDescending(x => x.CitationCount + x.ImageCount + x.TimelineEventCount)
            .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new WikiNavigationContext(related, backlinks, missing);
    }

    public async Task<WikiDashboardHighlights> GetDashboardHighlightsAsync(int month, int day, CancellationToken cancellationToken = default)
    {
        month = Math.Clamp(month, 1, 12);
        day = Math.Clamp(day, 1, DateTime.DaysInMonth(2000, month));
        var summaries = await BrowseAsync(new WikiBrowseQuery(Limit: 5000), cancellationToken).ConfigureAwait(false);
        var onThisDay = new List<WikiOnThisDayItem>();
        var eras = new Dictionary<int, (HashSet<Guid> Pages, int Events)>();
        foreach (var summary in summaries.Where(x => x.TimelineEventCount > 0))
        {
            var page = await GetPageAsync(summary.PageId, cancellationToken).ConfigureAwait(false);
            if (page is null) continue;
            foreach (var timelineEvent in page.Timeline)
            {
                if (timelineEvent.StartDate is { } date)
                {
                    if (date.Month == month && date.Day == day) onThisDay.Add(new WikiOnThisDayItem(summary, timelineEvent));
                    var decade = date.Year / 10 * 10;
                    if (!eras.TryGetValue(decade, out var era)) era = (new HashSet<Guid>(), 0);
                    era.Pages.Add(page.PageId);
                    eras[decade] = (era.Pages, era.Events + 1);
                }
            }
        }
        return new WikiDashboardHighlights(
            onThisDay.OrderBy(x => x.Event.StartDate?.Year).ThenBy(x => x.Event.Title).Take(30).ToArray(),
            eras.OrderBy(x => x.Key).Select(x => new WikiEraSummary(x.Key, x.Key + 9, x.Value.Events, x.Value.Pages.Count)).ToArray());
    }

    public async Task<IReadOnlyList<WikiTimelineShowSummary>> GetTimelineShowsAsync(CancellationToken cancellationToken = default)
    {
        var summaries = await BrowseAsync(new WikiBrowseQuery(PageType: "Show", Limit: 5000), cancellationToken).ConfigureAwait(false);
        var values = new List<WikiTimelineShowSummary>();
        foreach (var summary in summaries.Where(x => x.TimelineEventCount > 0))
        {
            var page = await GetPageAsync(summary.PageId, cancellationToken).ConfigureAwait(false);
            if (page is null) continue;
            var years = page.Timeline.Where(x => x.StartDate.HasValue).Select(x => x.StartDate!.Value.Year).ToArray();
            values.Add(new WikiTimelineShowSummary(summary, page.Timeline.Count,
                years.Length == 0 ? null : years.Min(), years.Length == 0 ? null : years.Max()));
        }
        return values.OrderBy(x => x.Page.Title, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<WikiQualityAuditReport> AuditQualityAsync(CancellationToken cancellationToken = default)
    {
        var citations = await AuditCitationsAsync(cancellationToken).ConfigureAwait(false);
        var graph = await LoadWikiGraphAsync(cancellationToken).ConfigureAwait(false);
        var incoming = new HashSet<Guid>();
        var outgoing = new HashSet<Guid>();
        var broken = new List<WikiMissingLink>();
        foreach (var page in graph.Documents.Values)
        {
            foreach (var relationship in page.Relationships)
            {
                outgoing.Add(page.PageId);
                incoming.Add(relationship.ToPageId);
            }
            foreach (var link in ExtractWikiLinks(page))
            {
                if (ResolveLink(graph.Index, link.Target) is { } target)
                {
                    outgoing.Add(page.PageId);
                    incoming.Add(target);
                }
                else broken.Add(new WikiMissingLink(page.PageId, page.Title, link.Target, link.Label));
            }
        }
        var orphans = graph.Summaries.Values
            .Where(x => !incoming.Contains(x.PageId) && !outgoing.Contains(x.PageId))
            .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        var duplicates = new List<WikiDuplicatePageCandidate>();
        var seenPairs = new HashSet<string>(StringComparer.Ordinal);
        var names = new Dictionary<string, List<WikiPageSummary>>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in graph.Documents.Values)
        {
            foreach (var name in new[] { page.Title }.Concat(page.Aliases).Select(NormalizeLinkKey).Where(x => x.Length > 0).Distinct())
            {
                if (!names.TryGetValue(name, out var list)) names[name] = list = new List<WikiPageSummary>();
                list.Add(graph.Summaries[page.PageId]);
            }
        }
        foreach (var pair in names.Where(x => x.Value.Select(p => p.PageId).Distinct().Count() > 1))
        {
            var pages = pair.Value.GroupBy(x => x.PageId).Select(x => x.First()).ToArray();
            for (var i = 0; i < pages.Length; i++)
            for (var j = i + 1; j < pages.Length; j++)
            {
                var key = string.CompareOrdinal(pages[i].PageId.ToString(), pages[j].PageId.ToString()) < 0
                    ? $"{pages[i].PageId}:{pages[j].PageId}" : $"{pages[j].PageId}:{pages[i].PageId}";
                if (seenPairs.Add(key)) duplicates.Add(new WikiDuplicatePageCandidate(pages[i], pages[j], $"Both pages use the name or alias '{pair.Key}'."));
            }
        }
        return new WikiQualityAuditReport(DateTimeOffset.UtcNow, citations, orphans, broken, duplicates);
    }

    public async Task<TopicCleanupReport> AuditTopicsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var terms = await ReadTopicTermsAsync(connection, cancellationToken).ConfigureAwait(false);
        var suggestions = new List<TopicMergeSuggestion>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in terms.Values.GroupBy(x => NormalizeTopicKey(x.Name)).Where(x => x.Key.Length > 0 && x.Count() > 1))
        {
            var values = group.OrderByDescending(TopicPreference).ToArray();
            AddTopicSuggestion(suggestions, seen, values[0].Name, values, 100, true,
                "The names differ only by capitalisation, punctuation, spacing or possessive wording.");
        }

        var tokenIndex = new Dictionary<string, List<TopicTerm>>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms.Values)
            foreach (var token in TopicTokens(term.Name))
            {
                if (!tokenIndex.TryGetValue(token, out var values)) tokenIndex[token] = values = new List<TopicTerm>();
                values.Add(term);
            }
        foreach (var term in terms.Values.OrderByDescending(TopicPreference).Take(2500))
        {
            var comparisons = TopicTokens(term.Name).SelectMany(token => tokenIndex[token]).Where(x => !ReferenceEquals(x, term)).Distinct().Take(250);
            foreach (var other in comparisons)
            {
                var score = TopicSimilarity(term.Name, other.Name);
                if (score < 70 || NormalizeTopicKey(term.Name) == NormalizeTopicKey(other.Name)) continue;
                var pair = new[] { term, other }.OrderByDescending(TopicPreference).ToArray();
                AddTopicSuggestion(suggestions, seen, pair[0].Name, pair, score, false,
                    score >= 92 ? "Very similar wording and topic terms." : "Related wording and shared topic terms; review before merging.");
            }
        }

        var history = await ReadTopicMergeHistoryAsync(connection, cancellationToken).ConfigureAwait(false);
        var canonicalCount = await ScalarIntAsync(connection, "SELECT COUNT(*) FROM canonical_topics", cancellationToken).ConfigureAwait(false);
        return new TopicCleanupReport(DateTimeOffset.UtcNow, terms.Count, canonicalCount,
            suggestions.OrderByDescending(x => x.SafeToAutomate).ThenByDescending(x => x.Confidence).ThenBy(x => x.CanonicalName).Take(500).ToArray(), history);
    }

    public async Task<TopicAutomaticCleanupResult> RunAutomaticTopicCleanupAsync(CancellationToken cancellationToken = default)
    {
        var report = await AuditTopicsAsync(cancellationToken).ConfigureAwait(false);
        var results = new List<TopicMergeResult>();
        foreach (var suggestion in report.Suggestions.Where(x => x.SafeToAutomate))
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await MergeTopicsAsync(new TopicMergeRequest(suggestion.CanonicalName, suggestion.Variants,
                suggestion.Confidence, suggestion.Reason, true, "Radio Vault automatic cleanup"), cancellationToken).ConfigureAwait(false));
        }
        return new TopicAutomaticCleanupResult(results.Count, results.Sum(x => x.ResearchRowsChanged),
            results.Sum(x => x.TagLinksChanged), results.Sum(x => x.WikiPagesArchived), results);
    }

    public async Task<TopicMergeResult> MergeTopicsAsync(TopicMergeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var canonicalName = RequireText(request.CanonicalName, "Choose a canonical topic name.", 240);
        var variants = new[] { canonicalName }.Concat(request.Variants ?? Array.Empty<string>())
            .Select(x => Limit(x, 240)).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (variants.Length < 2) throw new ArgumentException("A topic merge needs at least two distinct names.");
        var editor = string.IsNullOrWhiteSpace(request.Editor) ? "Radio Vault" : Limit(request.Editor, 200);
        var now = DateTimeOffset.UtcNow;
        var topicId = Guid.NewGuid();
        var mergeId = Guid.NewGuid();
        var researchChanged = 0;
        var tagLinksChanged = 0;
        var archivedPages = 0;

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandText = "SELECT id FROM canonical_topics WHERE canonical_name=$name COLLATE NOCASE OR normalized_key=$key LIMIT 1";
            existing.Parameters.AddWithValue("$name", canonicalName);
            existing.Parameters.AddWithValue("$key", NormalizeTopicKey(canonicalName));
            var value = await existing.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is string text && Guid.TryParse(text, out var parsed)) topicId = parsed;
        }
        await using (var canonical = connection.CreateCommand())
        {
            canonical.Transaction = transaction;
            canonical.CommandText = """
                INSERT INTO canonical_topics(id,canonical_name,normalized_key,created_at,updated_at)
                VALUES($id,$name,$key,$at,$at)
                ON CONFLICT(id) DO UPDATE SET canonical_name=excluded.canonical_name,normalized_key=excluded.normalized_key,updated_at=excluded.updated_at;
                """;
            canonical.Parameters.AddWithValue("$id", topicId.ToString("D"));
            canonical.Parameters.AddWithValue("$name", canonicalName);
            canonical.Parameters.AddWithValue("$key", NormalizeTopicKey(canonicalName));
            canonical.Parameters.AddWithValue("$at", now.ToString("O"));
            await canonical.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        foreach (var variant in variants)
        {
            await using var alias = connection.CreateCommand();
            alias.Transaction = transaction;
            alias.CommandText = """
                INSERT INTO canonical_topic_aliases(alias,normalized_key,topic_id,confidence,merge_kind,created_at)
                VALUES($alias,$key,$topic,$confidence,$kind,$at)
                ON CONFLICT(alias) DO UPDATE SET topic_id=excluded.topic_id,confidence=MAX(confidence,excluded.confidence),merge_kind=excluded.merge_kind;
                """;
            alias.Parameters.AddWithValue("$alias", variant);
            alias.Parameters.AddWithValue("$key", NormalizeTopicKey(variant));
            alias.Parameters.AddWithValue("$topic", topicId.ToString("D"));
            alias.Parameters.AddWithValue("$confidence", Math.Clamp(request.Confidence, 0, 100));
            alias.Parameters.AddWithValue("$kind", request.Automatic ? "automatic-safe" : "reviewed");
            alias.Parameters.AddWithValue("$at", now.ToString("O"));
            await alias.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var variant in variants.Where(x => !string.Equals(x, canonicalName, StringComparison.OrdinalIgnoreCase)))
        {
            researchChanged += await RewriteResearchTopicAsync(connection, transaction, variant, canonicalName, cancellationToken).ConfigureAwait(false);
            tagLinksChanged += await RewriteTagAsync(connection, transaction, variant, canonicalName, cancellationToken).ConfigureAwait(false);
        }
        archivedPages = await ConsolidateTopicWikiPagesAsync(connection, transaction, canonicalName, variants, mergeId, editor, now, cancellationToken).ConfigureAwait(false);

        var snapshot = JsonSerializer.Serialize(new { canonicalName, variants }, JsonOptions);
        await using (var history = connection.CreateCommand())
        {
            history.Transaction = transaction;
            history.CommandText = """
                INSERT INTO topic_merge_history(id,topic_id,canonical_name,aliases_json,reason,confidence,automatic,
                    affected_research_rows,affected_tag_links,archived_wiki_pages,snapshot_json,created_at,created_by)
                VALUES($id,$topic,$name,$aliases,$reason,$confidence,$automatic,$research,$tags,$pages,$snapshot,$at,$editor);
                """;
            history.Parameters.AddWithValue("$id", mergeId.ToString("D"));
            history.Parameters.AddWithValue("$topic", topicId.ToString("D"));
            history.Parameters.AddWithValue("$name", canonicalName);
            history.Parameters.AddWithValue("$aliases", JsonSerializer.Serialize(variants, JsonOptions));
            history.Parameters.AddWithValue("$reason", Limit(request.Reason, 1000));
            history.Parameters.AddWithValue("$confidence", Math.Clamp(request.Confidence, 0, 100));
            history.Parameters.AddWithValue("$automatic", request.Automatic ? 1 : 0);
            history.Parameters.AddWithValue("$research", researchChanged);
            history.Parameters.AddWithValue("$tags", tagLinksChanged);
            history.Parameters.AddWithValue("$pages", archivedPages);
            history.Parameters.AddWithValue("$snapshot", snapshot);
            history.Parameters.AddWithValue("$at", now.ToString("O"));
            history.Parameters.AddWithValue("$editor", editor);
            await history.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await using (var redirects = connection.CreateCommand())
        {
            redirects.Transaction = transaction;
            redirects.CommandText = "UPDATE wiki_page_redirects SET merge_history_id=$merge WHERE merge_history_id IS NULL AND created_at=$at";
            redirects.Parameters.AddWithValue("$merge", mergeId.ToString("D"));
            redirects.Parameters.AddWithValue("$at", now.ToString("O"));
            await redirects.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new TopicMergeResult(mergeId, topicId, canonicalName, variants.Length, researchChanged, tagLinksChanged,
            archivedPages, $"Merged {variants.Length:N0} topic names into '{canonicalName}' across {researchChanged + tagLinksChanged:N0} archive links and {archivedPages:N0} Wiki pages.");
    }

    public async Task<WikiPageSaveResult> SavePageAsync(WikiPageDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var title = RequireText(draft.Title, "A wiki page title is required.", 240);
        var slug = NormalizeSlug(string.IsNullOrWhiteSpace(draft.Slug) ? title : draft.Slug);
        var pageType = WikiPageTypes.Normalize(draft.PageType);
        var status = WikiPageStatuses.Normalize(draft.Status);
        var summary = Limit(draft.Summary, 4000);
        var markdown = Limit(draft.BodyMarkdown, 2_000_000);
        var editor = string.IsNullOrWhiteSpace(draft.Editor) ? "Radio Vault user" : Limit(draft.Editor, 200);
        var changeSummary = string.IsNullOrWhiteSpace(draft.ChangeSummary) ? "Edited page" : Limit(draft.ChangeSummary, 500);
        var pageId = draft.PageId is { } requested && requested != Guid.Empty ? requested : Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var existing = await ReadPageCoreAsync(connection, transaction, pageId, cancellationToken).ConfigureAwait(false);
        var created = existing is null;
        int revision;
        if (created)
        {
            if (draft.ExpectedRevision > 0)
                throw new WikiConcurrencyException("This wiki page no longer exists in the expected revision. Refresh before saving.");
            revision = 1;
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO wiki_pages(id,slug,title,page_type,summary,body_markdown,status,revision,created_at,updated_at,created_by,last_editor)
                VALUES($id,$slug,$title,$type,$summary,$body,$status,$revision,$created,$updated,$author,$editor);
                """;
            AddPageParameters(insert, pageId, slug, title, pageType, summary, markdown, status, revision, now, now, editor, editor);
            await ExecuteWithSlugMessageAsync(insert, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var current = existing ?? throw new InvalidOperationException("The wiki page could not be reloaded for editing.");
            if (draft.ExpectedRevision != current.Revision)
                throw new WikiConcurrencyException($"'{current.Title}' changed on another device. Reload revision {current.Revision} before saving.");
            revision = current.Revision + 1;
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                UPDATE wiki_pages
                   SET slug=$slug,title=$title,page_type=$type,summary=$summary,body_markdown=$body,
                       status=$status,revision=$revision,updated_at=$updated,last_editor=$editor
                 WHERE id=$id AND revision=$expected;
                """;
            update.Parameters.AddWithValue("$id", pageId.ToString("D"));
            update.Parameters.AddWithValue("$slug", slug);
            update.Parameters.AddWithValue("$title", title);
            update.Parameters.AddWithValue("$type", pageType);
            update.Parameters.AddWithValue("$summary", summary);
            update.Parameters.AddWithValue("$body", markdown);
            update.Parameters.AddWithValue("$status", status);
            update.Parameters.AddWithValue("$revision", revision);
            update.Parameters.AddWithValue("$updated", now.ToString("O"));
            update.Parameters.AddWithValue("$editor", editor);
            update.Parameters.AddWithValue("$expected", current.Revision);
            if (await ExecuteWithSlugMessageAsync(update, cancellationToken).ConfigureAwait(false) != 1)
                throw new WikiConcurrencyException("The wiki page changed while it was being saved. Reload and compare the newer revision.");
        }

        await ReplaceAliasesAsync(connection, transaction, pageId, draft.Aliases ?? Array.Empty<string>(), cancellationToken).ConfigureAwait(false);
        if (draft.Citations is not null || draft.Images is not null || draft.Timeline is not null)
        {
            var acceptedPage = new HashSet<Guid> { pageId };
            var citations = (draft.Citations ?? Array.Empty<WikiCitationRecord>())
                .Select(x => x with { PageId = pageId })
                .ToArray();
            var sources = citations.Where(x => x.Source is not null)
                .Select(x => x.Source!)
                .GroupBy(x => x.SourceId)
                .Select(x => x.Last())
                .ToArray();
            if (sources.Length > 0)
                await StoreSourcesAsync(connection, transaction, sources, cancellationToken).ConfigureAwait(false);
            if (draft.Citations is not null)
                await ReplaceCitationsAsync(connection, transaction, citations, acceptedPage, cancellationToken).ConfigureAwait(false);
            if (draft.Images is not null)
            {
                var imageDrafts = draft.Images.Select(x => x with { Link = x.Link with { PageId = pageId } }).ToArray();
                await StoreImageDraftsAsync(connection, transaction, imageDrafts, cancellationToken).ConfigureAwait(false);
                await ReplacePageImagesAsync(connection, transaction, imageDrafts.Select(x => x.Link with { Image = null }).ToArray(), acceptedPage, cancellationToken).ConfigureAwait(false);
            }
            if (draft.Timeline is not null)
            {
                var timeline = draft.Timeline.Select(x => x with
                {
                    PageId = pageId,
                    Broadcasts = x.Broadcasts.Select(link => link with { EventId = x.EventId }).ToArray()
                }).ToArray();
                await ReplaceTimelineAsync(connection, transaction, timeline, acceptedPage, cancellationToken).ConfigureAwait(false);
            }
        }
        await StoreRevisionAsync(
            connection, transaction, pageId, revision,
            new WikiRevisionSnapshot(slug, title, pageType, summary, markdown, status),
            changeSummary, editor, null, now, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WikiPageSaveResult(pageId, revision, now, created);
    }

    public async Task<WikiStarterPagePreview> PreviewStarterPagesAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var seeds = await ReadStarterSeedsAsync(connection, cancellationToken).ConfigureAwait(false);
        var existing = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT slug,page_type FROM wiki_pages";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) existing[reader.GetString(0)] = reader.GetString(1);
        }

        var candidates = new List<WikiStarterPageCandidate>(seeds.Count);
        foreach (var seed in seeds)
        {
            var slug = NormalizeSlug(seed.Title);
            if (existing.TryGetValue(slug, out var occupiedType) && !string.Equals(occupiedType, seed.PageType, StringComparison.OrdinalIgnoreCase))
                slug = NormalizeSlug($"{seed.PageType}-{seed.Title}");
            candidates.Add(new WikiStarterPageCandidate(seed.PageType, seed.Title, slug, seed.References, seed.Context, existing.ContainsKey(slug)));
        }
        return new WikiStarterPagePreview(
            candidates.Count(x => x.PageType == "Show" && !x.AlreadyExists),
            candidates.Count(x => x.PageType == "Person" && !x.AlreadyExists),
            candidates.Count(x => x.PageType == "Topic" && !x.AlreadyExists),
            candidates.Count(x => x.AlreadyExists),
            candidates);
    }

    public async Task<WikiStarterGenerationResult> GenerateStarterPagesAsync(CancellationToken cancellationToken = default)
    {
        var preview = await PreviewStarterPagesAsync(cancellationToken).ConfigureAwait(false);
        var created = new List<Guid>();
        foreach (var candidate in preview.Candidates.Where(x => !x.AlreadyExists))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var article = candidate.PageType switch
            {
                "Show" => $"# {candidate.Title}\n\n{candidate.Context}\n\n## History\n\nThis starter page was generated from the Radio Vault library. Add sourced history, dated images and major timeline events here.\n\n## Archive\n\nRadio Vault currently links {candidate.ArchiveReferences:N0} broadcasts to this show.",
                "Person" => $"# {candidate.Title}\n\n{candidate.Context}\n\n## Radio archive\n\nThis starter page was generated from people named in Radio Vault metadata. Confirm identity and roles with citations before publishing.",
                _ => $"# {candidate.Title}\n\n{candidate.Context}\n\n## In the archive\n\nThis starter page was generated from recurring Radio Vault topics. Add a sourced definition and link the most useful broadcasts or Moments."
            };
            try
            {
                var result = await SavePageAsync(new WikiPageDraft(
                    null, candidate.Slug, candidate.Title, candidate.PageType, candidate.Context,
                    article, "Draft", 0, "Generated archive starter page", "Radio Vault starter generator"), cancellationToken).ConfigureAwait(false);
                created.Add(result.PageId);
            }
            catch (InvalidOperationException exception) when (exception.Message.Contains("slug", StringComparison.OrdinalIgnoreCase))
            {
                // A page created concurrently wins; starter generation never overwrites it.
            }
        }
        var preserved = preview.Candidates.Count - created.Count;
        return new WikiStarterGenerationResult(created.Count, preserved, created,
            $"Created {created.Count:N0} starter pages and preserved {preserved:N0} existing pages without changing them.");
    }

    public async Task<WikiArchiveLinkResults> BrowseArchiveLinksAsync(
        WikiArchiveBrowseQuery query,
        CancellationToken cancellationToken = default)
    {
        query ??= new WikiArchiveBrowseQuery();
        var search = (query.Search ?? string.Empty).Trim();
        var limit = Math.Clamp(query.Limit, 1, 1000);
        var broadcasts = new List<WikiArchiveBroadcastCandidate>();
        var moments = new List<WikiArchiveMomentCandidate>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT e.id,c.id,c.name,e.title,e.air_date,COALESCE(e.broadcast_uid,''),
                       COALESCE((SELECT MAX(duration_ms) FROM media_files mf WHERE mf.episode_id=e.id),0)
                  FROM episodes e JOIN collections c ON c.id=e.collection_id
                 WHERE e.hidden=0 AND ($search='' OR e.title LIKE $like COLLATE NOCASE OR c.name LIKE $like COLLATE NOCASE
                       OR COALESCE(e.broadcast_uid,'') LIKE $like COLLATE NOCASE)
                 ORDER BY e.air_date DESC,c.name COLLATE NOCASE,e.title COLLATE NOCASE LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$search", search);
            command.Parameters.AddWithValue("$like", $"%{search}%");
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                broadcasts.Add(new WikiArchiveBroadcastCandidate(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                    ReadDate(reader, 4), reader.GetString(5), reader.GetInt64(6)));
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT m.id,e.id,c.name,e.title,m.title,m.position_ms,e.air_date
                  FROM moments m JOIN episodes e ON e.id=m.episode_id JOIN collections c ON c.id=e.collection_id
                 WHERE e.hidden=0 AND ($search='' OR m.title LIKE $like COLLATE NOCASE OR m.notes LIKE $like COLLATE NOCASE
                       OR e.title LIKE $like COLLATE NOCASE OR c.name LIKE $like COLLATE NOCASE)
                 ORDER BY e.air_date DESC,m.position_ms LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$search", search);
            command.Parameters.AddWithValue("$like", $"%{search}%");
            command.Parameters.AddWithValue("$limit", limit);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                moments.Add(new WikiArchiveMomentCandidate(reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetInt64(5), ReadDate(reader, 6)));
        }
        return new WikiArchiveLinkResults(broadcasts, moments);
    }

    public async Task<WikiAuthoringSnapshot> GetAuthoringSnapshotAsync(
        string appVersion,
        string databaseIdentity,
        CancellationToken cancellationToken = default)
    {
        var summaries = await BrowseAsync(new WikiBrowseQuery(Limit: 5000), cancellationToken).ConfigureAwait(false);
        var pages = new List<WikiAuthoringPageRecord>(summaries.Count);
        var markdown = new Dictionary<Guid, string>();
        var relationships = new Dictionary<Guid, WikiRelationshipRecord>();
        var sources = new Dictionary<Guid, WikiSourceRecord>();
        var citations = new Dictionary<Guid, WikiCitationRecord>();
        var images = new Dictionary<Guid, WikiImageRecord>();
        var pageImages = new List<WikiPageImageLink>();
        var timeline = new Dictionary<Guid, WikiTimelineEventRecord>();
        foreach (var summary in summaries)
        {
            var page = await GetPageAsync(summary.PageId, cancellationToken).ConfigureAwait(false);
            if (page is null) continue;
            pages.Add(new WikiAuthoringPageRecord(
                page.PageId, page.Revision, page.Slug, page.Title, page.PageType, page.Summary,
                page.Status, page.CreatedBy, page.LastEditor, page.Aliases));
            markdown[page.PageId] = page.BodyMarkdown;
            foreach (var value in page.Relationships) relationships[value.RelationshipId] = value;
            foreach (var value in page.Citations)
            {
                citations[value.CitationId] = value with { Source = null };
                if (value.Source is not null) sources[value.Source.SourceId] = value.Source;
            }
            foreach (var link in page.Images)
            {
                pageImages.Add(link with { Image = null });
                if (link.Image is not null) images[link.Image.ImageId] = link.Image;
            }
            foreach (var value in page.Timeline) timeline[value.EventId] = value;
        }

        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var timelineEvent in timeline.Values)
            foreach (var sourceId in timelineEvent.SourceIds)
                if (!sources.ContainsKey(sourceId))
                {
                    var source = await ReadSourceAsync(connection, sourceId, cancellationToken).ConfigureAwait(false);
                    if (source is not null) sources[sourceId] = source;
                }
        foreach (var image in images.Values)
            if (image.SourceId is { } sourceId && !sources.ContainsKey(sourceId))
            {
                var source = await ReadSourceAsync(connection, sourceId, cancellationToken).ConfigureAwait(false);
                if (source is not null) sources[sourceId] = source;
            }

        var imageBytes = new Dictionary<Guid, byte[]>();
        foreach (var imageId in images.Keys)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT content FROM wiki_images WHERE id=$id";
            command.Parameters.AddWithValue("$id", imageId.ToString("D"));
            var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (value is byte[] bytes) imageBytes[imageId] = bytes;
        }

        var imageRecords = images.Values.Select(x => new WikiAuthoringImageRecord(x, WikiAuthoringPackService.ImageArchivePath(x))).ToArray();
        var archiveContext = await ReadArchiveContextAsync(connection, cancellationToken).ConfigureAwait(false);
        var manifest = new WikiAuthoringPackManifest(
            WikiAuthoringPackService.SchemaVersion, appVersion, Guid.NewGuid(), DateTimeOffset.UtcNow,
            databaseIdentity, pages.Count, sources.Count, citations.Count, images.Count, timeline.Count,
            new Dictionary<string, string>());
        return new WikiAuthoringSnapshot(
            manifest, pages, markdown, relationships.Values.ToArray(), sources.Values.ToArray(),
            citations.Values.ToArray(), imageRecords, imageBytes, pageImages, timeline.Values.ToArray(), archiveContext);
    }

    public async Task<WikiPackPreview> PreviewImportAsync(
        WikiAuthoringSnapshot snapshot,
        string packageName,
        string packageSha256,
        CancellationToken cancellationToken = default)
    {
        ValidateSnapshot(snapshot);
        var newPages = 0;
        var changedPages = 0;
        var unchangedPages = 0;
        var conflicts = new List<string>();
        var changes = new List<WikiPackPageChangePreview>();
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        snapshot = await ResolveImportPageIdentitiesAsync(connection, null, snapshot, cancellationToken).ConfigureAwait(false);
        foreach (var incoming in snapshot.Pages)
        {
            var existing = await ReadPageCoreAsync(connection, null, incoming.PageId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                newPages++;
                changes.Add(new WikiPackPageChangePreview(incoming.PageId, incoming.Title, incoming.PageType, "Added",
                    incoming.BaseRevision, null, "This page is new and will be added."));
                continue;
            }
            var body = snapshot.PageMarkdown.GetValueOrDefault(incoming.PageId) ?? string.Empty;
            var same = PageContentEquals(existing, incoming, body);
            if (same)
            {
                unchangedPages++;
                changes.Add(new WikiPackPageChangePreview(incoming.PageId, incoming.Title, incoming.PageType, "Unchanged",
                    incoming.BaseRevision, existing.Revision, "The imported article matches the current Wiki page."));
                continue;
            }
            if (existing.Revision != incoming.BaseRevision)
            {
                conflicts.Add(incoming.Title);
                changes.Add(new WikiPackPageChangePreview(incoming.PageId, incoming.Title, incoming.PageType, "Protected",
                    incoming.BaseRevision, existing.Revision, $"Radio Vault has revision {existing.Revision}; the pack was based on revision {incoming.BaseRevision}. The newer human page will be preserved."));
            }
            else
            {
                changedPages++;
                changes.Add(new WikiPackPageChangePreview(incoming.PageId, incoming.Title, incoming.PageType, "Changed",
                    incoming.BaseRevision, existing.Revision, "The article differs and can be updated safely from its matching base revision."));
            }
        }
        var recoveredSourceTitles = snapshot.Sources.Count(source => string.IsNullOrWhiteSpace(source.Title));
        var summary = $"{newPages:N0} new, {changedPages:N0} changed, {unchangedPages:N0} unchanged and {conflicts.Count:N0} conflicting pages; " +
                      $"{snapshot.Sources.Count:N0} sources, {snapshot.Citations.Count:N0} citations, {snapshot.Images.Count:N0} images and {snapshot.TimelineEvents.Count:N0} timeline events." +
                      (recoveredSourceTitles > 0
                          ? $" Radio Vault will recover readable titles for {recoveredSourceTitles:N0} otherwise valid sources."
                          : string.Empty);
        return new WikiPackPreview(
            packageName, packageSha256, snapshot.Pages.Count, newPages, changedPages, unchangedPages, conflicts.Count,
            snapshot.Sources.Count, snapshot.Citations.Count, snapshot.Images.Count, snapshot.TimelineEvents.Count,
            conflicts.Take(100).ToArray(), snapshot.Pages.Count > 0, summary,
            changes.OrderBy(x => x.ChangeKind == "Protected" ? 0 : x.ChangeKind == "Changed" ? 1 : x.ChangeKind == "Added" ? 2 : 3)
                .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public async Task<WikiPackImportResult> ApplyImportAsync(
        WikiAuthoringSnapshot snapshot,
        string packageName,
        string packageSha256,
        CancellationToken cancellationToken = default,
        IProgress<WikiPackOperationProgress>? progress = null)
    {
        ValidateSnapshot(snapshot);
        await using var connection = await _database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        snapshot = await ResolveImportPageIdentitiesAsync(connection, transaction, snapshot, cancellationToken).ConfigureAwait(false);
        var now = DateTimeOffset.UtcNow;
        var importRunId = await StartImportRunAsync(connection, transaction, snapshot, packageName, packageSha256, now, cancellationToken).ConfigureAwait(false);
        var created = 0;
        var updated = 0;
        var unchanged = 0;
        var conflicts = 0;
        var acceptedPages = new HashSet<Guid>();

        for (var pageIndex = 0; pageIndex < snapshot.Pages.Count; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var incoming = snapshot.Pages[pageIndex];
            if (pageIndex == 0 || pageIndex == snapshot.Pages.Count - 1 || (pageIndex + 1) % 10 == 0)
                progress?.Report(new WikiPackOperationProgress(
                    snapshot.Pages.Count == 0 ? 75 : (pageIndex + 1) * 75d / snapshot.Pages.Count,
                    pageIndex + 1,
                    snapshot.Pages.Count,
                    "Importing Explore pages…"));
            var existing = await ReadPageCoreAsync(connection, transaction, incoming.PageId, cancellationToken).ConfigureAwait(false);
            var body = Limit(snapshot.PageMarkdown.GetValueOrDefault(incoming.PageId), 2_000_000);
            var slug = NormalizeSlug(string.IsNullOrWhiteSpace(incoming.Slug) ? incoming.Title : incoming.Slug);
            var title = RequireText(incoming.Title, "Every imported wiki page requires a title.", 240);
            var type = WikiPageTypes.Normalize(incoming.PageType);
            var status = WikiPageStatuses.Normalize(incoming.Status);
            var summary = Limit(incoming.Summary, 4000);
            var editor = string.IsNullOrWhiteSpace(incoming.LastEditor) ? "Imported authoring pack" : Limit(incoming.LastEditor, 200);
            int revision;
            if (existing is null)
            {
                revision = 1;
                await using var insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO wiki_pages(id,slug,title,page_type,summary,body_markdown,status,revision,created_at,updated_at,created_by,last_editor)
                    VALUES($id,$slug,$title,$type,$summary,$body,$status,$revision,$created,$updated,$author,$editor);
                    """;
                AddPageParameters(insert, incoming.PageId, slug, title, type, summary, body, status, revision, now, now,
                    string.IsNullOrWhiteSpace(incoming.CreatedBy) ? editor : Limit(incoming.CreatedBy, 200), editor);
                await ExecuteWithSlugMessageAsync(insert, cancellationToken).ConfigureAwait(false);
                created++;
            }
            else if (PageContentEquals(existing, incoming, body))
            {
                revision = existing.Revision;
                unchanged++;
            }
            else if (existing.Revision != incoming.BaseRevision)
            {
                conflicts++;
                continue;
            }
            else
            {
                revision = existing.Revision + 1;
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE wiki_pages SET slug=$slug,title=$title,page_type=$type,summary=$summary,
                        body_markdown=$body,status=$status,revision=$revision,updated_at=$updated,last_editor=$editor
                    WHERE id=$id AND revision=$expected;
                    """;
                update.Parameters.AddWithValue("$id", incoming.PageId.ToString("D"));
                update.Parameters.AddWithValue("$slug", slug);
                update.Parameters.AddWithValue("$title", title);
                update.Parameters.AddWithValue("$type", type);
                update.Parameters.AddWithValue("$summary", summary);
                update.Parameters.AddWithValue("$body", body);
                update.Parameters.AddWithValue("$status", status);
                update.Parameters.AddWithValue("$revision", revision);
                update.Parameters.AddWithValue("$updated", now.ToString("O"));
                update.Parameters.AddWithValue("$editor", editor);
                update.Parameters.AddWithValue("$expected", existing.Revision);
                if (await ExecuteWithSlugMessageAsync(update, cancellationToken).ConfigureAwait(false) != 1)
                    throw new WikiConcurrencyException($"'{title}' changed while the authoring pack was being imported.");
                updated++;
            }

            acceptedPages.Add(incoming.PageId);
            await ReplaceAliasesAsync(connection, transaction, incoming.PageId, incoming.Aliases, cancellationToken).ConfigureAwait(false);
            await StoreRevisionAsync(connection, transaction, incoming.PageId, revision,
                new WikiRevisionSnapshot(slug, title, type, summary, body, status),
                $"Imported from {packageName}", editor, importRunId, now, cancellationToken).ConfigureAwait(false);
        }

        progress?.Report(new WikiPackOperationProgress(80, snapshot.Pages.Count, snapshot.Pages.Count, "Storing Explore sources…"));
        var sourcesStored = await StoreSourcesAsync(connection, transaction, snapshot.Sources, cancellationToken).ConfigureAwait(false);
        progress?.Report(new WikiPackOperationProgress(85, snapshot.Pages.Count, snapshot.Pages.Count, "Storing embedded Explore images…"));
        var imagesStored = await StoreImagesAsync(connection, transaction, snapshot.Images, snapshot.ImageBytes, cancellationToken).ConfigureAwait(false);
        progress?.Report(new WikiPackOperationProgress(90, snapshot.Pages.Count, snapshot.Pages.Count, "Linking Explore relationships…"));
        await ReplaceRelationshipsAsync(connection, transaction, snapshot.Relationships, acceptedPages, cancellationToken).ConfigureAwait(false);
        progress?.Report(new WikiPackOperationProgress(93, snapshot.Pages.Count, snapshot.Pages.Count, "Linking Explore citations…"));
        var citationsStored = await ReplaceCitationsAsync(connection, transaction, snapshot.Citations, acceptedPages, cancellationToken).ConfigureAwait(false);
        await ReplacePageImagesAsync(connection, transaction, snapshot.PageImages, acceptedPages, cancellationToken).ConfigureAwait(false);
        progress?.Report(new WikiPackOperationProgress(96, snapshot.Pages.Count, snapshot.Pages.Count, "Building the Explore timeline…"));
        var timelineStored = await ReplaceTimelineAsync(connection, transaction, snapshot.TimelineEvents, acceptedPages, cancellationToken).ConfigureAwait(false);

        var resultSummary = $"Imported {created:N0} new and {updated:N0} changed pages; preserved {unchanged:N0} unchanged and skipped {conflicts:N0} conflicting pages.";
        await CompleteImportRunAsync(connection, transaction, importRunId, created, updated, unchanged, conflicts,
            sourcesStored, citationsStored, imagesStored, timelineStored, resultSummary, cancellationToken).ConfigureAwait(false);
        progress?.Report(new WikiPackOperationProgress(99, snapshot.Pages.Count, snapshot.Pages.Count, "Committing Explore history…"));
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WikiPackImportResult(created, updated, unchanged, conflicts, sourcesStored, citationsStored,
            imagesStored, timelineStored, importRunId, resultSummary);
    }

    private static async Task<WikiPageCore?> ReadPageCoreAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        Guid pageId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,slug,title,page_type,summary,body_markdown,status,revision,
                   created_at,updated_at,created_by,last_editor
              FROM wiki_pages WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", pageId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new WikiPageCore(
            ReadGuid(reader, 0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7),
            ReadRequiredDateTime(reader, 8), ReadRequiredDateTime(reader, 9), reader.GetString(10), reader.GetString(11));
    }

    private static async Task<WikiPageCore?> ReadPageCoreBySlugAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string slug,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id,slug,title,page_type,summary,body_markdown,status,revision,
                   created_at,updated_at,created_by,last_editor
              FROM wiki_pages WHERE slug=$slug COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$slug", slug);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        return new WikiPageCore(
            ReadGuid(reader, 0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), reader.GetInt32(7),
            ReadRequiredDateTime(reader, 8), ReadRequiredDateTime(reader, 9), reader.GetString(10), reader.GetString(11));
    }

    private static async Task<WikiAuthoringSnapshot> ResolveImportPageIdentitiesAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        WikiAuthoringSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var pageMap = new Dictionary<Guid, Guid>();
        var targetIds = new HashSet<Guid>();
        foreach (var page in snapshot.Pages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await ReadPageCoreAsync(connection, transaction, page.PageId, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                var slug = NormalizeSlug(string.IsNullOrWhiteSpace(page.Slug) ? page.Title : page.Slug);
                existing = await ReadPageCoreBySlugAsync(connection, transaction, slug, cancellationToken).ConfigureAwait(false);
            }
            var target = existing?.PageId ?? page.PageId;
            if (!targetIds.Add(target))
                throw new InvalidDataException($"More than one imported page resolves to the existing Explore page '{page.Title}'. Merge the duplicate pages in the pack before importing it.");
            pageMap[page.PageId] = target;
        }

        if (pageMap.All(entry => entry.Key == entry.Value)) return snapshot;
        Guid Page(Guid value) => pageMap.GetValueOrDefault(value, value);
        var pages = snapshot.Pages.Select(page => page with { PageId = Page(page.PageId) }).ToArray();
        var markdown = snapshot.PageMarkdown.ToDictionary(entry => Page(entry.Key), entry => entry.Value);
        var relationships = snapshot.Relationships.Select(value => value with
        {
            FromPageId = Page(value.FromPageId),
            ToPageId = Page(value.ToPageId)
        }).ToArray();
        var citations = snapshot.Citations.Select(value => value with { PageId = Page(value.PageId) }).ToArray();
        var pageImages = snapshot.PageImages.Select(value => value with { PageId = Page(value.PageId) }).ToArray();
        var timeline = snapshot.TimelineEvents.Select(value => value with { PageId = Page(value.PageId) }).ToArray();
        return snapshot with
        {
            Pages = pages,
            PageMarkdown = markdown,
            Relationships = relationships,
            Citations = citations,
            PageImages = pageImages,
            TimelineEvents = timeline
        };
    }

    private static async Task<IReadOnlyList<string>> ReadAliasesAsync(SqliteConnection connection, Guid pageId, CancellationToken token)
    {
        var values = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT alias FROM wiki_page_aliases WHERE page_id=$id ORDER BY sort_order,alias COLLATE NOCASE";
        command.Parameters.AddWithValue("$id", pageId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) values.Add(reader.GetString(0));
        return values;
    }

    private static async Task<IReadOnlyList<WikiRelationshipRecord>> ReadRelationshipsAsync(SqliteConnection connection, Guid pageId, CancellationToken token)
    {
        var values = new List<WikiRelationshipRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,from_page_id,to_page_id,relationship_type,valid_from,valid_to,date_precision,notes,sort_order
              FROM wiki_relationships WHERE from_page_id=$id ORDER BY sort_order,id;
            """;
        command.Parameters.AddWithValue("$id", pageId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            values.Add(new WikiRelationshipRecord(ReadGuid(reader, 0), ReadGuid(reader, 1), ReadGuid(reader, 2),
                reader.GetString(3), ReadDate(reader, 4), ReadDate(reader, 5), reader.GetString(6), reader.GetString(7), reader.GetInt32(8)));
        return values;
    }

    private static async Task<IReadOnlyList<WikiCitationRecord>> ReadCitationsAsync(SqliteConnection connection, Guid pageId, CancellationToken token)
    {
        var values = new List<WikiCitationRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id,c.page_id,c.source_id,c.ordinal,c.section_anchor,c.quoted_text,c.note,
                   s.source_type,s.title,s.author,s.publisher,s.url,s.archived_url,s.published_date,s.date_precision,
                   s.accessed_at,s.episode_id,s.broadcast_uid,s.start_ms,s.end_ms,s.transcript_segment_id,s.moment_id,s.locator,s.notes
              FROM wiki_citations c JOIN wiki_sources s ON s.id=c.source_id
             WHERE c.page_id=$id ORDER BY c.ordinal,c.id;
            """;
        command.Parameters.AddWithValue("$id", pageId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var sourceId = ReadGuid(reader, 2);
            var source = new WikiSourceRecord(sourceId, reader.GetString(7), reader.GetString(8), reader.GetString(9),
                reader.GetString(10), reader.GetString(11), reader.GetString(12), ReadDate(reader, 13), reader.GetString(14),
                ReadDateTime(reader, 15), ReadNullableInt64(reader, 16), reader.GetString(17), ReadNullableInt64(reader, 18),
                ReadNullableInt64(reader, 19), ReadNullableInt64(reader, 20), ReadNullableInt64(reader, 21), reader.GetString(22), reader.GetString(23));
            values.Add(new WikiCitationRecord(ReadGuid(reader, 0), ReadGuid(reader, 1), sourceId, reader.GetInt32(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6), source));
        }
        return values;
    }

    private static async Task<IReadOnlyList<WikiPageImageLink>> ReadPageImagesAsync(SqliteConnection connection, Guid pageId, CancellationToken token)
    {
        var values = new List<WikiPageImageLink>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT pi.page_id,pi.image_id,pi.role,pi.sort_order,
                   i.original_file_name,i.media_type,i.sha256,i.byte_count,i.caption,i.alt_text,i.creator,
                   i.copyright_holder,i.licence,i.source_id,i.captured_date,i.representative_from,
                   i.representative_to,i.date_precision,i.date_notes
              FROM wiki_page_images pi JOIN wiki_images i ON i.id=pi.image_id
             WHERE pi.page_id=$id ORDER BY CASE pi.role WHEN 'Lead' THEN 0 ELSE 1 END,pi.sort_order;
            """;
        command.Parameters.AddWithValue("$id", pageId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            var imageId = ReadGuid(reader, 1);
            var image = new WikiImageRecord(imageId, reader.GetString(4), reader.GetString(5), reader.GetString(6),
                reader.GetInt64(7), reader.GetString(8), reader.GetString(9), reader.GetString(10), reader.GetString(11),
                reader.GetString(12), ReadNullableGuid(reader, 13), ReadDate(reader, 14), ReadDate(reader, 15),
                ReadDate(reader, 16), reader.GetString(17), reader.GetString(18));
            values.Add(new WikiPageImageLink(ReadGuid(reader, 0), imageId, reader.GetString(2), reader.GetInt32(3), image));
        }
        return values;
    }

    private static async Task<IReadOnlyList<WikiTimelineEventRecord>> ReadTimelineAsync(SqliteConnection connection, Guid pageId, CancellationToken token)
    {
        var eventRows = new List<(Guid EventId, Guid PageId, string Title, string Summary, string Category,
            DateOnly? StartDate, DateOnly? EndDate, string DatePrecision, string DateDisplay, int Significance, int SortOrder)>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,page_id,title,summary,category,start_date,end_date,date_precision,date_display,significance,sort_order
              FROM wiki_timeline_events WHERE page_id=$id
             ORDER BY CASE WHEN start_date IS NULL THEN 1 ELSE 0 END,start_date,sort_order,title COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$id", pageId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
        {
            eventRows.Add((ReadGuid(reader, 0), ReadGuid(reader, 1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), ReadDate(reader, 5), ReadDate(reader, 6), reader.GetString(7), reader.GetString(8),
                reader.GetInt32(9), reader.GetInt32(10)));
        }

        await reader.DisposeAsync().ConfigureAwait(false);
        var values = new List<WikiTimelineEventRecord>(eventRows.Count);
        foreach (var row in eventRows)
        {
            var sourceIds = await ReadLinkedIdsAsync(connection, "wiki_timeline_event_sources", "source_id", row.EventId, token).ConfigureAwait(false);
            var imageIds = await ReadLinkedIdsAsync(connection, "wiki_timeline_event_images", "image_id", row.EventId, token).ConfigureAwait(false);
            var broadcasts = await ReadTimelineBroadcastsAsync(connection, row.EventId, token).ConfigureAwait(false);
            values.Add(new WikiTimelineEventRecord(row.EventId, row.PageId, row.Title, row.Summary, row.Category,
                row.StartDate, row.EndDate, row.DatePrecision, row.DateDisplay, row.Significance, row.SortOrder,
                sourceIds, imageIds, broadcasts));
        }
        return values;
    }

    private static async Task<IReadOnlyList<Guid>> ReadLinkedIdsAsync(SqliteConnection connection, string table, string column, Guid eventId, CancellationToken token)
    {
        var values = new List<Guid>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {column} FROM {table} WHERE event_id=$id ORDER BY sort_order,{column}";
        command.Parameters.AddWithValue("$id", eventId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false)) values.Add(ReadGuid(reader, 0));
        return values;
    }

    private static async Task<IReadOnlyList<WikiTimelineBroadcastLink>> ReadTimelineBroadcastsAsync(SqliteConnection connection, Guid eventId, CancellationToken token)
    {
        var values = new List<WikiTimelineBroadcastLink>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT event_id,episode_id,moment_id,start_ms,end_ms,label,sort_order
              FROM wiki_timeline_event_broadcasts WHERE event_id=$id ORDER BY sort_order,episode_id,start_ms;
            """;
        command.Parameters.AddWithValue("$id", eventId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            values.Add(new WikiTimelineBroadcastLink(ReadGuid(reader, 0), reader.GetInt64(1), ReadNullableInt64(reader, 2),
                ReadNullableInt64(reader, 3), ReadNullableInt64(reader, 4), reader.GetString(5), reader.GetInt32(6)));
        return values;
    }

    private static async Task<WikiSourceRecord?> ReadSourceAsync(SqliteConnection connection, Guid sourceId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,source_type,title,author,publisher,url,archived_url,published_date,date_precision,accessed_at,
                   episode_id,broadcast_uid,start_ms,end_ms,transcript_segment_id,moment_id,locator,notes
              FROM wiki_sources WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$id", sourceId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        return new WikiSourceRecord(ReadGuid(reader, 0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
            reader.GetString(4), reader.GetString(5), reader.GetString(6), ReadDate(reader, 7), reader.GetString(8),
            ReadDateTime(reader, 9), ReadNullableInt64(reader, 10), reader.GetString(11), ReadNullableInt64(reader, 12),
            ReadNullableInt64(reader, 13), ReadNullableInt64(reader, 14), ReadNullableInt64(reader, 15), reader.GetString(16), reader.GetString(17));
    }

    private static void ValidateSnapshot(WikiAuthoringSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Manifest.SchemaVersion != WikiAuthoringPackService.SchemaVersion)
            throw new InvalidDataException($"This wiki pack uses schema {snapshot.Manifest.SchemaVersion}; Radio Vault supports schema {WikiAuthoringPackService.SchemaVersion}.");
        if (snapshot.Pages.Count > 50_000) throw new InvalidDataException("The wiki pack contains too many pages.");
        if (snapshot.Pages.Select(x => x.PageId).Any(x => x == Guid.Empty) || snapshot.Pages.Select(x => x.PageId).Distinct().Count() != snapshot.Pages.Count)
            throw new InvalidDataException("Every wiki page must have a unique stable ID.");
        var pageIds = snapshot.Pages.Select(x => x.PageId).ToHashSet();
        if (snapshot.PageMarkdown.Keys.Any(x => !pageIds.Contains(x))) throw new InvalidDataException("The pack contains Markdown for an unknown page.");
        if (snapshot.Citations.Any(x => !pageIds.Contains(x.PageId))) throw new InvalidDataException("A citation refers to an unknown page.");
        if (snapshot.TimelineEvents.Any(x => !pageIds.Contains(x.PageId))) throw new InvalidDataException("A timeline event refers to an unknown page.");
    }

    private static bool PageContentEquals(WikiPageCore existing, WikiAuthoringPageRecord incoming, string body)
        => string.Equals(existing.Slug, NormalizeSlug(string.IsNullOrWhiteSpace(incoming.Slug) ? incoming.Title : incoming.Slug), StringComparison.OrdinalIgnoreCase)
           && string.Equals(existing.Title, incoming.Title?.Trim(), StringComparison.Ordinal)
           && string.Equals(existing.PageType, WikiPageTypes.Normalize(incoming.PageType), StringComparison.Ordinal)
           && string.Equals(existing.Summary, incoming.Summary ?? string.Empty, StringComparison.Ordinal)
           && string.Equals(existing.BodyMarkdown, body ?? string.Empty, StringComparison.Ordinal)
           && string.Equals(existing.Status, WikiPageStatuses.Normalize(incoming.Status), StringComparison.Ordinal);

    private static async Task ReplaceAliasesAsync(SqliteConnection connection, SqliteTransaction transaction, Guid pageId, IReadOnlyList<string> aliases, CancellationToken token)
    {
        await DeleteForPageAsync(connection, transaction, "wiki_page_aliases", pageId, token).ConfigureAwait(false);
        var order = 0;
        foreach (var alias in (aliases ?? Array.Empty<string>()).Select(x => Limit(x, 240)).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO wiki_page_aliases(page_id,alias,sort_order) VALUES($page,$alias,$sort)";
            command.Parameters.AddWithValue("$page", pageId.ToString("D"));
            command.Parameters.AddWithValue("$alias", alias);
            command.Parameters.AddWithValue("$sort", order++);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task<int> StoreSourcesAsync(SqliteConnection connection, SqliteTransaction transaction, IReadOnlyList<WikiSourceRecord> sources, CancellationToken token)
    {
        var count = 0;
        foreach (var source in sources)
        {
            if (source.SourceId == Guid.Empty) throw new InvalidDataException("Every source requires a stable ID.");
            var episodeId = await ExistingIdOrNullAsync(connection, transaction, "episodes", source.EpisodeId, token).ConfigureAwait(false);
            var momentId = await ExistingIdOrNullAsync(connection, transaction, "moments", source.MomentId, token).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO wiki_sources(id,source_type,title,author,publisher,url,archived_url,published_date,date_precision,
                    accessed_at,episode_id,broadcast_uid,start_ms,end_ms,transcript_segment_id,moment_id,locator,notes)
                VALUES($id,$type,$title,$author,$publisher,$url,$archive,$published,$precision,$accessed,$episode,$broadcast,
                    $start,$end,$segment,$moment,$locator,$notes)
                ON CONFLICT(id) DO UPDATE SET source_type=excluded.source_type,title=excluded.title,author=excluded.author,
                    publisher=excluded.publisher,url=excluded.url,archived_url=excluded.archived_url,published_date=excluded.published_date,
                    date_precision=excluded.date_precision,accessed_at=excluded.accessed_at,episode_id=excluded.episode_id,
                    broadcast_uid=excluded.broadcast_uid,start_ms=excluded.start_ms,end_ms=excluded.end_ms,
                    transcript_segment_id=excluded.transcript_segment_id,moment_id=excluded.moment_id,locator=excluded.locator,notes=excluded.notes;
                """;
            command.Parameters.AddWithValue("$id", source.SourceId.ToString("D"));
            command.Parameters.AddWithValue("$type", Limit(source.SourceType, 80));
            command.Parameters.AddWithValue("$title", ResolveImportedSourceTitle(source));
            command.Parameters.AddWithValue("$author", Limit(source.Author, 300));
            command.Parameters.AddWithValue("$publisher", Limit(source.Publisher, 300));
            command.Parameters.AddWithValue("$url", Limit(source.Url, 4000));
            command.Parameters.AddWithValue("$archive", Limit(source.ArchivedUrl, 4000));
            command.Parameters.AddWithValue("$published", DbDate(source.PublishedDate));
            command.Parameters.AddWithValue("$precision", Limit(source.DatePrecision, 40));
            command.Parameters.AddWithValue("$accessed", source.AccessedAt?.ToString("O") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$episode", episodeId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$broadcast", Limit(source.BroadcastUid, 300));
            command.Parameters.AddWithValue("$start", source.StartMs ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$end", source.EndMs ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$segment", source.TranscriptSegmentId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$moment", momentId ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$locator", Limit(source.Locator, 1000));
            command.Parameters.AddWithValue("$notes", Limit(source.Notes, 8000));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            count++;
        }
        return count;
    }

    private static async Task StoreImageDraftsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<WikiImageDraft> drafts,
        CancellationToken token)
    {
        var records = new List<WikiAuthoringImageRecord>(drafts.Count);
        var bytesById = new Dictionary<Guid, byte[]>();
        foreach (var draft in drafts)
        {
            var image = draft.Link.Image ?? throw new InvalidDataException("Every page image requires image metadata.");
            var bytes = draft.Content;
            if (bytes is null)
            {
                await using var read = connection.CreateCommand();
                read.Transaction = transaction;
                read.CommandText = "SELECT content FROM wiki_images WHERE id=$id";
                read.Parameters.AddWithValue("$id", image.ImageId.ToString("D"));
                bytes = await read.ExecuteScalarAsync(token).ConfigureAwait(false) as byte[]
                    ?? throw new InvalidDataException($"Choose an image file for '{image.OriginalFileName}'.");
            }
            var normalized = image with
            {
                Sha256 = WikiAuthoringPackService.Sha256(bytes),
                ByteCount = bytes.LongLength
            };
            records.Add(new WikiAuthoringImageRecord(normalized, WikiAuthoringPackService.ImageArchivePath(normalized)));
            bytesById[normalized.ImageId] = bytes;
        }
        await StoreImagesAsync(connection, transaction, records, bytesById, token).ConfigureAwait(false);
    }

    private static async Task<int> StoreImagesAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<WikiAuthoringImageRecord> images, IReadOnlyDictionary<Guid, byte[]> imageBytes, CancellationToken token)
    {
        var count = 0;
        foreach (var record in images)
        {
            var image = record.Image;
            if (!imageBytes.TryGetValue(image.ImageId, out var bytes)) throw new InvalidDataException($"Image {image.ImageId:D} has no archive file.");
            var hash = WikiAuthoringPackService.Sha256(bytes);
            if (!string.Equals(hash, image.Sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Image {image.OriginalFileName} failed its checksum.");
            if (bytes.LongLength != image.ByteCount) throw new InvalidDataException($"Image {image.OriginalFileName} has the wrong byte count.");
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO wiki_images(id,original_file_name,media_type,sha256,byte_count,content,caption,alt_text,creator,
                    copyright_holder,licence,source_id,captured_date,representative_from,representative_to,date_precision,date_notes)
                VALUES($id,$name,$media,$hash,$bytes,$content,$caption,$alt,$creator,$copyright,$licence,$source,$captured,$from,$to,$precision,$notes)
                ON CONFLICT(id) DO UPDATE SET original_file_name=excluded.original_file_name,media_type=excluded.media_type,
                    sha256=excluded.sha256,byte_count=excluded.byte_count,content=excluded.content,caption=excluded.caption,
                    alt_text=excluded.alt_text,creator=excluded.creator,copyright_holder=excluded.copyright_holder,
                    licence=excluded.licence,source_id=excluded.source_id,captured_date=excluded.captured_date,
                    representative_from=excluded.representative_from,representative_to=excluded.representative_to,
                    date_precision=excluded.date_precision,date_notes=excluded.date_notes;
                """;
            command.Parameters.AddWithValue("$id", image.ImageId.ToString("D"));
            command.Parameters.AddWithValue("$name", Limit(image.OriginalFileName, 500));
            command.Parameters.AddWithValue("$media", Limit(image.MediaType, 120));
            command.Parameters.AddWithValue("$hash", hash);
            command.Parameters.AddWithValue("$bytes", bytes.LongLength);
            command.Parameters.AddWithValue("$content", bytes);
            command.Parameters.AddWithValue("$caption", Limit(image.Caption, 4000));
            command.Parameters.AddWithValue("$alt", Limit(image.AltText, 4000));
            command.Parameters.AddWithValue("$creator", Limit(image.Creator, 500));
            command.Parameters.AddWithValue("$copyright", Limit(image.CopyrightHolder, 500));
            command.Parameters.AddWithValue("$licence", Limit(image.Licence, 500));
            command.Parameters.AddWithValue("$source", image.SourceId?.ToString("D") ?? (object)DBNull.Value);
            command.Parameters.AddWithValue("$captured", DbDate(image.CapturedDate));
            command.Parameters.AddWithValue("$from", DbDate(image.RepresentativeFrom));
            command.Parameters.AddWithValue("$to", DbDate(image.RepresentativeTo));
            command.Parameters.AddWithValue("$precision", Limit(image.DatePrecision, 40));
            command.Parameters.AddWithValue("$notes", Limit(image.DateNotes, 4000));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            count++;
        }
        return count;
    }

    private static async Task ReplaceRelationshipsAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<WikiRelationshipRecord> relationships, IReadOnlySet<Guid> pages, CancellationToken token)
    {
        foreach (var pageId in pages) await DeleteForPageAsync(connection, transaction, "wiki_relationships", pageId, token, "from_page_id").ConfigureAwait(false);
        foreach (var value in relationships.Where(x => pages.Contains(x.FromPageId)))
        {
            if (!await PageExistsAsync(connection, transaction, value.ToPageId, token).ConfigureAwait(false)) continue;
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO wiki_relationships(id,from_page_id,to_page_id,relationship_type,valid_from,valid_to,date_precision,notes,sort_order)
                VALUES($id,$from,$to,$type,$validFrom,$validTo,$precision,$notes,$sort);
                """;
            command.Parameters.AddWithValue("$id", value.RelationshipId.ToString("D"));
            command.Parameters.AddWithValue("$from", value.FromPageId.ToString("D"));
            command.Parameters.AddWithValue("$to", value.ToPageId.ToString("D"));
            command.Parameters.AddWithValue("$type", Limit(value.RelationshipType, 120));
            command.Parameters.AddWithValue("$validFrom", DbDate(value.ValidFrom));
            command.Parameters.AddWithValue("$validTo", DbDate(value.ValidTo));
            command.Parameters.AddWithValue("$precision", Limit(value.DatePrecision, 40));
            command.Parameters.AddWithValue("$notes", Limit(value.Notes, 4000));
            command.Parameters.AddWithValue("$sort", value.SortOrder);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task<int> ReplaceCitationsAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<WikiCitationRecord> citations, IReadOnlySet<Guid> pages, CancellationToken token)
    {
        foreach (var pageId in pages) await DeleteForPageAsync(connection, transaction, "wiki_citations", pageId, token).ConfigureAwait(false);
        var count = 0;
        foreach (var value in citations.Where(x => pages.Contains(x.PageId)))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO wiki_citations(id,page_id,source_id,ordinal,section_anchor,quoted_text,note) VALUES($id,$page,$source,$ordinal,$section,$quote,$note)";
            command.Parameters.AddWithValue("$id", value.CitationId.ToString("D"));
            command.Parameters.AddWithValue("$page", value.PageId.ToString("D"));
            command.Parameters.AddWithValue("$source", value.SourceId.ToString("D"));
            command.Parameters.AddWithValue("$ordinal", Math.Max(1, value.Ordinal));
            command.Parameters.AddWithValue("$section", Limit(value.SectionAnchor, 300));
            command.Parameters.AddWithValue("$quote", Limit(value.QuotedText, 20_000));
            command.Parameters.AddWithValue("$note", Limit(value.Note, 8000));
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            count++;
        }
        return count;
    }

    private static async Task ReplacePageImagesAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<WikiPageImageLink> links, IReadOnlySet<Guid> pages, CancellationToken token)
    {
        foreach (var pageId in pages) await DeleteForPageAsync(connection, transaction, "wiki_page_images", pageId, token).ConfigureAwait(false);
        foreach (var value in links.Where(x => pages.Contains(x.PageId)))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO wiki_page_images(page_id,image_id,role,sort_order) VALUES($page,$image,$role,$sort)";
            command.Parameters.AddWithValue("$page", value.PageId.ToString("D"));
            command.Parameters.AddWithValue("$image", value.ImageId.ToString("D"));
            command.Parameters.AddWithValue("$role", Limit(value.Role, 80));
            command.Parameters.AddWithValue("$sort", value.SortOrder);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
    }

    private static async Task<int> ReplaceTimelineAsync(SqliteConnection connection, SqliteTransaction transaction,
        IReadOnlyList<WikiTimelineEventRecord> events, IReadOnlySet<Guid> pages, CancellationToken token)
    {
        foreach (var pageId in pages) await DeleteForPageAsync(connection, transaction, "wiki_timeline_events", pageId, token).ConfigureAwait(false);
        var count = 0;
        foreach (var value in events.Where(x => pages.Contains(x.PageId)))
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO wiki_timeline_events(id,page_id,title,summary,category,start_date,end_date,date_precision,date_display,significance,sort_order)
                    VALUES($id,$page,$title,$summary,$category,$start,$end,$precision,$display,$significance,$sort);
                    """;
                command.Parameters.AddWithValue("$id", value.EventId.ToString("D"));
                command.Parameters.AddWithValue("$page", value.PageId.ToString("D"));
                command.Parameters.AddWithValue("$title", RequireText(value.Title, "Every timeline event requires a title.", 500));
                command.Parameters.AddWithValue("$summary", Limit(value.Summary, 20_000));
                command.Parameters.AddWithValue("$category", Limit(value.Category, 120));
                command.Parameters.AddWithValue("$start", DbDate(value.StartDate));
                command.Parameters.AddWithValue("$end", DbDate(value.EndDate));
                command.Parameters.AddWithValue("$precision", Limit(value.DatePrecision, 40));
                command.Parameters.AddWithValue("$display", Limit(value.DateDisplay, 200));
                command.Parameters.AddWithValue("$significance", Math.Clamp(value.Significance, 0, 100));
                command.Parameters.AddWithValue("$sort", value.SortOrder);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            var order = 0;
            foreach (var sourceId in value.SourceIds.Distinct())
            {
                await InsertEventLinkAsync(connection, transaction, "wiki_timeline_event_sources", "source_id", value.EventId, sourceId, order++, token).ConfigureAwait(false);
            }
            order = 0;
            foreach (var imageId in value.ImageIds.Distinct())
            {
                await InsertEventLinkAsync(connection, transaction, "wiki_timeline_event_images", "image_id", value.EventId, imageId, order++, token).ConfigureAwait(false);
            }
            foreach (var link in value.Broadcasts)
            {
                var episodeId = await ExistingIdOrNullAsync(connection, transaction, "episodes", link.EpisodeId, token).ConfigureAwait(false);
                if (episodeId is null) continue;
                var momentId = await ExistingIdOrNullAsync(connection, transaction, "moments", link.MomentId, token).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO wiki_timeline_event_broadcasts(event_id,episode_id,moment_id,start_ms,end_ms,label,sort_order)
                    VALUES($event,$episode,$moment,$start,$end,$label,$sort);
                    """;
                command.Parameters.AddWithValue("$event", value.EventId.ToString("D"));
                command.Parameters.AddWithValue("$episode", episodeId);
                command.Parameters.AddWithValue("$moment", momentId ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$start", link.StartMs ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$end", link.EndMs ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("$label", Limit(link.Label, 500));
                command.Parameters.AddWithValue("$sort", link.SortOrder);
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            count++;
        }
        return count;
    }

    private static async Task InsertEventLinkAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, string column, Guid eventId, Guid targetId, int order, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"INSERT OR IGNORE INTO {table}(event_id,{column},sort_order) VALUES($event,$target,$sort)";
        command.Parameters.AddWithValue("$event", eventId.ToString("D"));
        command.Parameters.AddWithValue("$target", targetId.ToString("D"));
        command.Parameters.AddWithValue("$sort", order);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<long> StartImportRunAsync(SqliteConnection connection, SqliteTransaction transaction,
        WikiAuthoringSnapshot snapshot, string name, string hash, DateTimeOffset now, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO wiki_import_runs(package_name,package_sha256,package_id,schema_version,imported_at)
            VALUES($name,$hash,$id,$schema,$at); SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$name", Limit(name, 500));
        command.Parameters.AddWithValue("$hash", hash);
        command.Parameters.AddWithValue("$id", snapshot.Manifest.PackageId.ToString("D"));
        command.Parameters.AddWithValue("$schema", snapshot.Manifest.SchemaVersion);
        command.Parameters.AddWithValue("$at", now.ToString("O"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
    }

    private static async Task CompleteImportRunAsync(SqliteConnection connection, SqliteTransaction transaction, long id,
        int created, int updated, int unchanged, int conflicts, int sources, int citations, int images, int timeline,
        string summary, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE wiki_import_runs SET created_pages=$created,updated_pages=$updated,unchanged_pages=$unchanged,
                skipped_conflicts=$conflicts,sources_stored=$sources,citations_stored=$citations,images_stored=$images,
                timeline_events_stored=$timeline,summary_json=$summary WHERE id=$id;
            """;
        command.Parameters.AddWithValue("$created", created);
        command.Parameters.AddWithValue("$updated", updated);
        command.Parameters.AddWithValue("$unchanged", unchanged);
        command.Parameters.AddWithValue("$conflicts", conflicts);
        command.Parameters.AddWithValue("$sources", sources);
        command.Parameters.AddWithValue("$citations", citations);
        command.Parameters.AddWithValue("$images", images);
        command.Parameters.AddWithValue("$timeline", timeline);
        command.Parameters.AddWithValue("$summary", JsonSerializer.Serialize(new { message = summary }, JsonOptions));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task StoreRevisionAsync(SqliteConnection connection, SqliteTransaction transaction,
        Guid pageId, int revision, WikiRevisionSnapshot snapshot, string changeSummary, string author,
        long? importRunId, DateTimeOffset createdAt, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO wiki_page_revisions(page_id,revision,snapshot_json,change_summary,author,import_run_id,created_at)
            VALUES($page,$revision,$snapshot,$summary,$author,$import,$created);
            """;
        command.Parameters.AddWithValue("$page", pageId.ToString("D"));
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(snapshot, JsonOptions));
        command.Parameters.AddWithValue("$summary", changeSummary);
        command.Parameters.AddWithValue("$author", author);
        command.Parameters.AddWithValue("$import", importRunId ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$created", createdAt.ToString("O"));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static void AddPageParameters(SqliteCommand command, Guid id, string slug, string title, string type,
        string summary, string body, string status, int revision, DateTimeOffset created, DateTimeOffset updated,
        string author, string editor)
    {
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        command.Parameters.AddWithValue("$slug", slug);
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$revision", revision);
        command.Parameters.AddWithValue("$created", created.ToString("O"));
        command.Parameters.AddWithValue("$updated", updated.ToString("O"));
        command.Parameters.AddWithValue("$author", author);
        command.Parameters.AddWithValue("$editor", editor);
    }

    private static async Task<int> ExecuteWithSlugMessageAsync(SqliteCommand command, CancellationToken token)
    {
        try { return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false); }
        catch (SqliteException exception) when (exception.SqliteErrorCode == 19 && exception.Message.Contains("wiki_pages.slug", StringComparison.OrdinalIgnoreCase))
        { throw new InvalidOperationException("Another wiki page already uses this slug. Choose a different page address.", exception); }
    }

    private static async Task DeleteForPageAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, Guid pageId, CancellationToken token, string column = "page_id")
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"DELETE FROM {table} WHERE {column}=$page";
        command.Parameters.AddWithValue("$page", pageId.ToString("D"));
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private static async Task<bool> PageExistsAsync(SqliteConnection connection, SqliteTransaction transaction, Guid pageId, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT COUNT(*) FROM wiki_pages WHERE id=$id";
        command.Parameters.AddWithValue("$id", pageId.ToString("D"));
        return Convert.ToInt64(await command.ExecuteScalarAsync(token).ConfigureAwait(false)) > 0;
    }

    private static async Task<object?> ExistingIdOrNullAsync(SqliteConnection connection, SqliteTransaction transaction,
        string table, long? id, CancellationToken token)
    {
        if (id is null or <= 0) return null;
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"SELECT id FROM {table} WHERE id=$id";
        command.Parameters.AddWithValue("$id", id.Value);
        return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is null ? null : id.Value;
    }

    private static async Task<List<StarterSeed>> ReadStarterSeedsAsync(SqliteConnection connection, CancellationToken token)
    {
        var values = new List<StarterSeed>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT c.name,COUNT(e.id),MIN(e.air_date),MAX(e.air_date)
                  FROM collections c LEFT JOIN episodes e ON e.collection_id=c.id AND e.hidden=0
                 GROUP BY c.id,c.name ORDER BY c.name COLLATE NOCASE;
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var count = reader.GetInt32(1);
                var first = ReadDate(reader, 2);
                var last = ReadDate(reader, 3);
                var range = first is null ? "Its broadcasts are currently undated." : first == last
                    ? $"The archive currently dates this show to {first:yyyy}."
                    : $"The archive currently spans {first:yyyy}–{last:yyyy}.";
                values.Add(new StarterSeed("Show", name, count, $"Radio Vault contains {count:N0} broadcasts for {name}. {range}"));
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH names(name,refs) AS (
                    SELECT g.name,COUNT(*) FROM guests g JOIN episode_guests eg ON eg.guest_id=g.id GROUP BY g.id,g.name
                    UNION ALL
                    SELECT rp.name,COUNT(*) FROM research_people rp WHERE TRIM(rp.name)<>'' GROUP BY rp.name COLLATE NOCASE
                )
                SELECT name,SUM(refs) FROM names WHERE TRIM(name)<>'' GROUP BY name COLLATE NOCASE
                HAVING SUM(refs)>=2 ORDER BY SUM(refs) DESC,name COLLATE NOCASE LIMIT 1000;
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var count = reader.GetInt32(1);
                values.Add(new StarterSeed("Person", name, count, $"{name} appears in metadata for {count:N0} Radio Vault broadcasts."));
            }
        }
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                WITH names(name,refs) AS (
                    SELECT t.name,COUNT(*) FROM tags t JOIN episode_tags et ON et.tag_id=t.id GROUP BY t.id,t.name
                    UNION ALL
                    SELECT rt.topic,COUNT(*) FROM research_topics rt WHERE TRIM(rt.topic)<>'' GROUP BY rt.topic COLLATE NOCASE
                )
                SELECT name,SUM(refs) FROM names WHERE TRIM(name)<>'' GROUP BY name COLLATE NOCASE
                HAVING SUM(refs)>=2 ORDER BY SUM(refs) DESC,name COLLATE NOCASE LIMIT 1000;
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var count = reader.GetInt32(1);
                values.Add(new StarterSeed("Topic", name, count, $"{name} is indexed across {count:N0} Radio Vault broadcasts."));
            }
        }
        return values;
    }

    private static async Task<WikiArchiveContext> ReadArchiveContextAsync(SqliteConnection connection, CancellationToken token)
    {
        var shows = new List<WikiArchiveShowContext>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT c.id,c.name,COUNT(e.id),MIN(e.air_date),MAX(e.air_date)
                  FROM collections c LEFT JOIN episodes e ON e.collection_id=c.id AND e.hidden=0
                 GROUP BY c.id,c.name ORDER BY c.name COLLATE NOCASE;
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                shows.Add(new WikiArchiveShowContext(reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), ReadDate(reader, 3), ReadDate(reader, 4)));
        }

        var broadcasts = new List<WikiArchiveBroadcastContext>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT e.id,c.id,c.name,e.title,e.air_date,COALESCE(e.broadcast_uid,''),
                       COALESCE((SELECT MAX(duration_ms) FROM media_files mf WHERE mf.episode_id=e.id),0),
                       CASE WHEN tr.id IS NULL THEN 0 ELSE 1 END,
                       COALESCE((SELECT COUNT(*) FROM transcript_segments ts WHERE ts.transcript_id=tr.id),0),
                       COALESCE((SELECT GROUP_CONCAT(name,CHAR(31)) FROM (
                           SELECT DISTINCT rp.name AS name FROM research_people rp JOIN research_broadcasts rb ON rb.id=rp.research_broadcast_id WHERE rb.episode_id=e.id AND TRIM(rp.name)<>''
                           UNION SELECT DISTINCT g.name FROM guests g JOIN episode_guests eg ON eg.guest_id=g.id WHERE eg.episode_id=e.id)),''),
                       COALESCE((SELECT GROUP_CONCAT(topic,CHAR(31)) FROM (
                           SELECT DISTINCT rt.topic AS topic FROM research_topics rt JOIN research_broadcasts rb ON rb.id=rt.research_broadcast_id WHERE rb.episode_id=e.id AND TRIM(rt.topic)<>''
                           UNION SELECT DISTINCT t.name FROM tags t JOIN episode_tags et ON et.tag_id=t.id WHERE et.episode_id=e.id)), '')
                  FROM episodes e JOIN collections c ON c.id=e.collection_id
                  LEFT JOIN transcripts tr ON tr.episode_id=e.id
                 WHERE e.hidden=0 ORDER BY c.name COLLATE NOCASE,e.air_date,e.id;
                """;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
                broadcasts.Add(new WikiArchiveBroadcastContext(
                    reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3), ReadDate(reader, 4),
                    reader.GetString(5), reader.GetInt64(6), reader.GetInt32(7) == 1, reader.GetInt32(8),
                    SplitContextValues(reader.GetString(9)), SplitContextValues(reader.GetString(10))));
        }
        var transcriptCount = broadcasts.Count(x => x.HasTranscript);
        var segmentCount = broadcasts.Sum(x => x.TranscriptSegments);
        return new WikiArchiveContext(DateTimeOffset.UtcNow, shows, broadcasts, transcriptCount, segmentCount);
    }

    private static IReadOnlyList<string> SplitContextValues(string value)
        => string.IsNullOrWhiteSpace(value)
            ? Array.Empty<string>()
            : value.Split((char)31, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task<WikiGraph> LoadWikiGraphAsync(CancellationToken cancellationToken)
    {
        var summaries = (await BrowseAsync(new WikiBrowseQuery(Limit: 5000), cancellationToken).ConfigureAwait(false))
            .ToDictionary(x => x.PageId);
        var documents = new Dictionary<Guid, WikiPageDocument>();
        var index = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var summary in summaries.Values)
        {
            var page = await GetPageAsync(summary.PageId, cancellationToken).ConfigureAwait(false);
            if (page is null) continue;
            documents[page.PageId] = page;
            foreach (var key in new[] { page.Slug, page.Title }.Concat(page.Aliases))
            {
                var normalized = NormalizeLinkKey(key);
                if (normalized.Length > 0) index.TryAdd(normalized, page.PageId);
            }
        }
        return new WikiGraph(summaries, documents, index);
    }

    private static IEnumerable<WikiLinkToken> ExtractWikiLinks(WikiPageDocument page)
    {
        foreach (Match match in WikiLinkPattern.Matches(page.BodyMarkdown ?? string.Empty))
        {
            var target = match.Groups["target"].Success ? match.Groups["target"].Value : match.Groups["target2"].Value;
            var label = match.Groups["label"].Success ? match.Groups["label"].Value
                : match.Groups["label2"].Success ? match.Groups["label2"].Value : target;
            if (!string.IsNullOrWhiteSpace(target)) yield return new WikiLinkToken(target.Trim(), label.Trim());
        }
    }

    private static Guid? ResolveLink(IReadOnlyDictionary<string, Guid> index, string target)
    {
        var normalized = NormalizeLinkKey(target);
        return index.TryGetValue(normalized, out var pageId) ? pageId : null;
    }

    private static async Task<Dictionary<string, TopicTerm>> ReadTopicTermsAsync(SqliteConnection connection, CancellationToken token)
    {
        var values = new Dictionary<string, TopicTerm>(StringComparer.Ordinal);
        async Task AddAsync(string sql, bool wiki)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var name = reader.GetString(0).Trim();
                if (name.Length == 0) continue;
                if (!values.TryGetValue(name, out var term)) term = new TopicTerm(name, 0, 0);
                values[name] = wiki ? term with { WikiPages = term.WikiPages + reader.GetInt32(1) }
                    : term with { References = term.References + reader.GetInt32(1) };
            }
        }
        await AddAsync("SELECT topic,COUNT(*) FROM research_topics WHERE TRIM(topic)<>'' GROUP BY topic", false).ConfigureAwait(false);
        await AddAsync("SELECT t.name,COUNT(et.episode_id) FROM tags t JOIN episode_tags et ON et.tag_id=t.id WHERE TRIM(t.name)<>'' GROUP BY t.id,t.name", false).ConfigureAwait(false);
        await AddAsync("SELECT title,COUNT(*) FROM wiki_pages WHERE page_type='Topic' AND status<>'Archived' GROUP BY title", true).ConfigureAwait(false);
        return values;
    }

    private static async Task<IReadOnlyList<TopicMergeHistoryRecord>> ReadTopicMergeHistoryAsync(SqliteConnection connection, CancellationToken token)
    {
        var values = new List<TopicMergeHistoryRecord>();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,topic_id,canonical_name,aliases_json,reason,confidence,automatic,affected_research_rows,
                   affected_tag_links,archived_wiki_pages,created_at,created_by
              FROM topic_merge_history ORDER BY created_at DESC LIMIT 100;
            """;
        await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
        while (await reader.ReadAsync(token).ConfigureAwait(false))
            values.Add(new TopicMergeHistoryRecord(ReadGuid(reader, 0), ReadGuid(reader, 1), reader.GetString(2),
                JsonSerializer.Deserialize<string[]>(reader.GetString(3), JsonOptions) ?? Array.Empty<string>(), reader.GetString(4),
                reader.GetInt32(5), reader.GetInt32(6) == 1, reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9),
                ReadRequiredDateTime(reader, 10), reader.GetString(11)));
        return values;
    }

    private static void AddTopicSuggestion(List<TopicMergeSuggestion> output, HashSet<string> seen, string canonical,
        IReadOnlyList<TopicTerm> terms, int confidence, bool automatic, string reason)
    {
        var variants = terms.Select(x => x.Name).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x).ToArray();
        if (variants.Length < 2) return;
        var key = string.Join("\u001f", variants.Select(NormalizeTopicKey).OrderBy(x => x));
        if (!seen.Add(key)) return;
        output.Add(new TopicMergeSuggestion(canonical, variants, confidence, automatic,
            terms.Sum(x => x.References), terms.Sum(x => x.WikiPages), reason));
    }

    private static int TopicPreference(TopicTerm value)
        => value.WikiPages * 10000 + value.References * 10 + (value.Name.Any(char.IsUpper) ? 5 : 0) - value.Name.Length;

    private static string NormalizeTopicKey(string? value)
    {
        var text = (value ?? string.Empty).Normalize(NormalizationForm.FormD).ToLowerInvariant();
        text = Regex.Replace(text, @"\b([\p{L}\p{N}]+)[’']s\b", "$1");
        var characters = text.Where(x => CharUnicodeInfo.GetUnicodeCategory(x) != UnicodeCategory.NonSpacingMark)
            .Select(x => char.IsLetterOrDigit(x) ? x : ' ').ToArray();
        var tokens = new string(characters).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        if (tokens.Count > 1 && tokens[0] == "the") tokens.RemoveAt(0);
        return string.Join(' ', tokens);
    }

    private static IReadOnlyList<string> TopicTokens(string value)
        => NormalizeTopicKey(value).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(x => x.Length > 2 && x is not "and" and not "with" and not "from" and not "about")
            .Select(x => x.Length > 4 && x.EndsWith('s') ? x[..^1] : x).Distinct().ToArray();

    private static int TopicSimilarity(string left, string right)
    {
        var a = TopicTokens(left).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var b = TopicTokens(right).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersection = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
        var union = a.Union(b, StringComparer.OrdinalIgnoreCase).Count();
        var jaccard = 100d * intersection / Math.Max(1, union);
        var containment = 100d * intersection / Math.Max(1, Math.Min(a.Count, b.Count));
        return (int)Math.Round(jaccard * .55 + containment * .45);
    }

    private static async Task<int> RewriteResearchTopicAsync(SqliteConnection connection, SqliteTransaction transaction,
        string variant, string canonical, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id,research_broadcast_id FROM research_topics WHERE topic=$variant COLLATE NOCASE";
        command.Parameters.AddWithValue("$variant", variant);
        var rows = new List<(long Id, long ResearchId)>();
        await using (var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false))
            while (await reader.ReadAsync(token).ConfigureAwait(false)) rows.Add((reader.GetInt64(0), reader.GetInt64(1)));
        foreach (var row in rows)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = """
                DELETE FROM research_topics WHERE id=$id AND EXISTS(
                    SELECT 1 FROM research_topics WHERE research_broadcast_id=$research AND topic=$canonical COLLATE NOCASE AND id<>$id);
                UPDATE research_topics SET topic=$canonical WHERE id=$id;
                """;
            update.Parameters.AddWithValue("$id", row.Id);
            update.Parameters.AddWithValue("$research", row.ResearchId);
            update.Parameters.AddWithValue("$canonical", canonical);
            await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        return rows.Count;
    }

    private static async Task<int> RewriteTagAsync(SqliteConnection connection, SqliteTransaction transaction,
        string variant, string canonical, CancellationToken token)
    {
        long canonicalId;
        await using (var ensure = connection.CreateCommand())
        {
            ensure.Transaction = transaction;
            ensure.CommandText = "INSERT OR IGNORE INTO tags(name) VALUES($name); SELECT id FROM tags WHERE name=$name COLLATE NOCASE LIMIT 1";
            ensure.Parameters.AddWithValue("$name", canonical);
            canonicalId = Convert.ToInt64(await ensure.ExecuteScalarAsync(token).ConfigureAwait(false));
        }
        var sourceIds = new List<long>();
        await using (var source = connection.CreateCommand())
        {
            source.Transaction = transaction;
            source.CommandText = "SELECT id FROM tags WHERE name=$name COLLATE NOCASE AND id<>$canonical";
            source.Parameters.AddWithValue("$name", variant);
            source.Parameters.AddWithValue("$canonical", canonicalId);
            await using var reader = await source.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false)) sourceIds.Add(reader.GetInt64(0));
        }
        var changed = 0;
        foreach (var sourceId in sourceIds)
        {
            await using var move = connection.CreateCommand();
            move.Transaction = transaction;
            move.CommandText = """
                INSERT OR IGNORE INTO episode_tags(episode_id,tag_id) SELECT episode_id,$canonical FROM episode_tags WHERE tag_id=$source;
                DELETE FROM episode_tags WHERE tag_id=$source;
                DELETE FROM tags WHERE id=$source;
                """;
            move.Parameters.AddWithValue("$canonical", canonicalId);
            move.Parameters.AddWithValue("$source", sourceId);
            changed += await move.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        return changed;
    }

    private static async Task<int> ConsolidateTopicWikiPagesAsync(SqliteConnection connection, SqliteTransaction transaction,
        string canonicalName, IReadOnlyList<string> variants, Guid mergeId, string editor, DateTimeOffset now, CancellationToken token)
    {
        var pages = new Dictionary<Guid, WikiPageCore>();
        foreach (var variant in variants)
        {
            await using var find = connection.CreateCommand();
            find.Transaction = transaction;
            find.CommandText = """
                SELECT DISTINCT p.id,p.slug,p.title,p.page_type,p.summary,p.body_markdown,p.status,p.revision,p.created_at,p.updated_at,p.created_by,p.last_editor
                  FROM wiki_pages p LEFT JOIN wiki_page_aliases a ON a.page_id=p.id
                 WHERE p.page_type='Topic' AND p.status<>'Archived' AND (p.title=$name COLLATE NOCASE OR a.alias=$name COLLATE NOCASE);
                """;
            find.Parameters.AddWithValue("$name", variant);
            await using var reader = await find.ExecuteReaderAsync(token).ConfigureAwait(false);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                var page = new WikiPageCore(ReadGuid(reader, 0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetString(5), reader.GetString(6), reader.GetInt32(7), ReadRequiredDateTime(reader, 8), ReadRequiredDateTime(reader, 9), reader.GetString(10), reader.GetString(11));
                pages[page.PageId] = page;
            }
        }
        if (pages.Count == 0) return 0;
        var survivor = pages.Values.OrderByDescending(x => string.Equals(x.Title, canonicalName, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(x => x.Revision).ThenByDescending(x => x.BodyMarkdown.Length).First();
        var body = survivor.BodyMarkdown;
        foreach (var loser in pages.Values.Where(x => x.PageId != survivor.PageId))
        {
            if (!string.IsNullOrWhiteSpace(loser.BodyMarkdown) && !body.Contains(loser.BodyMarkdown, StringComparison.Ordinal))
                body += $"\n\n## Material merged from {loser.Title}\n\n{loser.BodyMarkdown}";
            await using var move = connection.CreateCommand();
            move.Transaction = transaction;
            move.CommandText = """
                INSERT OR IGNORE INTO wiki_page_aliases(page_id,alias,sort_order) VALUES($survivor,$title,999);
                INSERT OR IGNORE INTO wiki_page_aliases(page_id,alias,sort_order) SELECT $survivor,alias,sort_order+1000 FROM wiki_page_aliases WHERE page_id=$loser;
                UPDATE wiki_citations SET page_id=$survivor WHERE page_id=$loser;
                INSERT OR IGNORE INTO wiki_page_images(page_id,image_id,role,sort_order) SELECT $survivor,image_id,role,sort_order+1000 FROM wiki_page_images WHERE page_id=$loser;
                DELETE FROM wiki_page_images WHERE page_id=$loser;
                UPDATE wiki_timeline_events SET page_id=$survivor WHERE page_id=$loser;
                UPDATE wiki_relationships SET from_page_id=$survivor WHERE from_page_id=$loser;
                UPDATE wiki_relationships SET to_page_id=$survivor WHERE to_page_id=$loser;
                DELETE FROM wiki_relationships WHERE from_page_id=to_page_id;
                UPDATE wiki_pages SET status='Archived',summary=$redirect,updated_at=$at,last_editor=$editor WHERE id=$loser;
                INSERT OR REPLACE INTO wiki_page_redirects(from_page_id,to_page_id,merge_history_id,created_at) VALUES($loser,$survivor,NULL,$at);
                """;
            move.Parameters.AddWithValue("$survivor", survivor.PageId.ToString("D"));
            move.Parameters.AddWithValue("$loser", loser.PageId.ToString("D"));
            move.Parameters.AddWithValue("$title", loser.Title);
            move.Parameters.AddWithValue("$redirect", $"Merged into {canonicalName}. The page and its revision history are preserved.");
            move.Parameters.AddWithValue("$at", now.ToString("O"));
            move.Parameters.AddWithValue("$editor", editor);
            await move.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await using (var renumberCitations = connection.CreateCommand())
        {
            renumberCitations.Transaction = transaction;
            renumberCitations.CommandText = """
                WITH ordered(id,next_ordinal) AS (
                    SELECT id,ROW_NUMBER() OVER (ORDER BY ordinal,id)
                      FROM wiki_citations
                     WHERE page_id=$page
                )
                UPDATE wiki_citations
                   SET ordinal=(SELECT next_ordinal FROM ordered WHERE ordered.id=wiki_citations.id)
                 WHERE page_id=$page;
                """;
            renumberCitations.Parameters.AddWithValue("$page", survivor.PageId.ToString("D"));
            await renumberCitations.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        foreach (var variant in variants)
        {
            await using var alias = connection.CreateCommand();
            alias.Transaction = transaction;
            alias.CommandText = "INSERT OR IGNORE INTO wiki_page_aliases(page_id,alias,sort_order) VALUES($page,$alias,999)";
            alias.Parameters.AddWithValue("$page", survivor.PageId.ToString("D"));
            alias.Parameters.AddWithValue("$alias", variant);
            await alias.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        var revision = survivor.Revision + 1;
        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE wiki_pages SET title=$title,body_markdown=$body,revision=$revision,updated_at=$at,last_editor=$editor WHERE id=$id";
            update.Parameters.AddWithValue("$title", canonicalName);
            update.Parameters.AddWithValue("$body", body);
            update.Parameters.AddWithValue("$revision", revision);
            update.Parameters.AddWithValue("$at", now.ToString("O"));
            update.Parameters.AddWithValue("$editor", editor);
            update.Parameters.AddWithValue("$id", survivor.PageId.ToString("D"));
            await update.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        await StoreRevisionAsync(connection, transaction, survivor.PageId, revision,
            new WikiRevisionSnapshot(survivor.Slug, canonicalName, "Topic", survivor.Summary, body, survivor.Status),
            $"Consolidated topic aliases: {string.Join(", ", variants)}", editor, null, now, token).ConfigureAwait(false);
        await using (var canonicalPage = connection.CreateCommand())
        {
            canonicalPage.Transaction = transaction;
            canonicalPage.CommandText = "UPDATE canonical_topics SET canonical_wiki_page_id=$page,updated_at=$at WHERE canonical_name=$name COLLATE NOCASE";
            canonicalPage.Parameters.AddWithValue("$page", survivor.PageId.ToString("D"));
            canonicalPage.Parameters.AddWithValue("$at", now.ToString("O"));
            canonicalPage.Parameters.AddWithValue("$name", canonicalName);
            await canonicalPage.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }
        return Math.Max(0, pages.Count - 1);
    }

    private static async Task<int> ScalarIntAsync(SqliteConnection connection, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(await command.ExecuteScalarAsync(token).ConfigureAwait(false));
    }

    private static string NormalizeLinkKey(string? value)
    {
        var text = Uri.UnescapeDataString((value ?? string.Empty).Trim());
        if (text.StartsWith("wiki:", StringComparison.OrdinalIgnoreCase)) text = text[5..];
        var characters = text.ToLowerInvariant().Select(x => char.IsLetterOrDigit(x) ? x : ' ').ToArray();
        return string.Join(' ', new string(characters).Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static Guid ReadGuid(SqliteDataReader reader, int ordinal) => Guid.Parse(reader.GetString(ordinal));
    private static Guid? ReadNullableGuid(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) || !Guid.TryParse(reader.GetString(ordinal), out var value) ? null : value;
    private static long? ReadNullableInt64(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);
    private static DateOnly? ReadDate(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) || !DateOnly.TryParse(reader.GetString(ordinal), out var value) ? null : value;
    private static DateTimeOffset? ReadDateTime(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) || !DateTimeOffset.TryParse(reader.GetString(ordinal), out var value) ? null : value;
    private static DateTimeOffset ReadRequiredDateTime(SqliteDataReader reader, int ordinal)
        => DateTimeOffset.TryParse(reader.GetString(ordinal), out var value) ? value : DateTimeOffset.MinValue;
    private static object DbDate(DateOnly? value) => value?.ToString("yyyy-MM-dd") ?? (object)DBNull.Value;

    private static string NormalizeSlug(string value)
    {
        var chars = (value ?? string.Empty).Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length == 0) slug = "page-" + Guid.NewGuid().ToString("N")[..8];
        return slug.Length <= 160 ? slug : slug[..160].TrimEnd('-');
    }

    private static string RequireText(string? value, string message, int max)
    {
        var normalized = Limit(value, max);
        return normalized.Length == 0 ? throw new ArgumentException(message) : normalized;
    }

    private static string ResolveImportedSourceTitle(WikiSourceRecord source)
    {
        var title = Limit(source.Title, 500);
        if (title.Length > 0) return title;

        var publisher = Limit(source.Publisher, 300);
        if (publisher.Length > 0) return Limit($"Source from {publisher}", 500);

        foreach (var candidate in new[] { source.Url, source.ArchivedUrl })
        {
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) continue;
            var host = uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
            if (host.Length > 0) return Limit($"Source from {host}", 500);
        }

        if (source.MomentId is > 0) return $"Radio Vault Moment #{source.MomentId.Value}";
        if (source.EpisodeId is > 0) return $"Radio Vault broadcast #{source.EpisodeId.Value}";
        if (!string.IsNullOrWhiteSpace(source.BroadcastUid)) return Limit($"Radio Vault broadcast {source.BroadcastUid}", 500);
        if (!string.IsNullOrWhiteSpace(source.Locator)) return Limit(source.Locator, 500);
        if (!string.IsNullOrWhiteSpace(source.SourceType)) return Limit($"{source.SourceType} source", 500);
        return "Untitled source";
    }

    private static string Limit(string? value, int max)
    {
        var normalized = (value ?? string.Empty).Trim();
        return normalized.Length <= max ? normalized : normalized[..max];
    }

    private sealed record WikiPageCore(
        Guid PageId, string Slug, string Title, string PageType, string Summary, string BodyMarkdown,
        string Status, int Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string CreatedBy, string LastEditor);

    private sealed record WikiRevisionSnapshot(
        string Slug, string Title, string PageType, string Summary, string BodyMarkdown, string Status);

    private sealed record StarterSeed(string PageType, string Title, int References, string Context);
    private sealed record TopicTerm(string Name, int References, int WikiPages);
    private sealed record WikiLinkToken(string Target, string Label);
    private sealed record WikiGraph(
        IReadOnlyDictionary<Guid, WikiPageSummary> Summaries,
        IReadOnlyDictionary<Guid, WikiPageDocument> Documents,
        IReadOnlyDictionary<string, Guid> Index);
}
