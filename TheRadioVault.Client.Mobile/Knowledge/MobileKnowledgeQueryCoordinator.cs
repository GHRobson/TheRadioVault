using TheRadioVault.Client.Mobile.Models;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile.Knowledge;

/// <summary>
/// Owns cache-first Knowledge queries, live snapshot refresh, date-review
/// decisions and deterministic coverage derived from the saved Library.
/// </summary>
internal sealed class MobileKnowledgeQueryCoordinator
{
    private readonly IMobileKnowledgeTransport _transport;
    private readonly MobileMetadataCache _cache;
    private readonly Func<DateTimeOffset> _utcNow;

    public MobileKnowledgeQueryCoordinator(
        IMobileKnowledgeTransport transport,
        MobileMetadataCache cache,
        Func<DateTimeOffset>? utcNow = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public MobileKnowledgeSnapshot? Knowledge { get; private set; }
    public string Status { get; private set; } = "Knowledge has not been loaded yet.";

    public void AdoptCachedSnapshot() => Knowledge = _cache.Snapshot.Knowledge;

    public void Clear()
    {
        Knowledge = null;
        Status = "Knowledge has not been loaded yet.";
    }

    public async Task<MobileKnowledgeSnapshot?> LoadAsync(
        bool isPaired,
        bool isLiveConnected,
        CancellationToken cancellationToken = default)
    {
        Knowledge = _cache.Snapshot.Knowledge ?? BuildLibrarySnapshot();
        if (Knowledge is not null)
            Status = Knowledge.IsLibraryFallback
                ? $"Library coverage ready · {Knowledge.Overview.TotalRecords:N0} broadcasts"
                : $"Saved Knowledge snapshot · {Knowledge.Overview.TotalRecords:N0} records";
        if (!isPaired)
        {
            Status = "Pair this iPhone with a Radio Vault Server to load Knowledge.";
            return Knowledge;
        }
        if (!isLiveConnected)
        {
            Status = Knowledge is null
                ? "Knowledge has not been saved on this iPhone yet. Reconnect to the server and try again."
                : Knowledge.IsLibraryFallback
                    ? "Offline · showing coverage from the saved Library catalogue."
                    : "Offline · showing the latest saved Knowledge snapshot.";
            return Knowledge;
        }
        try
        {
            var overviewTask = _transport.GetOverviewAsync(cancellationToken);
            var collectionsTask = _transport.GetCollectionsAsync(cancellationToken);
            var reviewsTask = _transport.GetDateReviewsAsync(cancellationToken);
            await Task.WhenAll(overviewTask, collectionsTask, reviewsTask).ConfigureAwait(false);
            Knowledge = new MobileKnowledgeSnapshot(
                await overviewTask.ConfigureAwait(false),
                await collectionsTask.ConfigureAwait(false),
                await reviewsTask.ConfigureAwait(false),
                _utcNow());
            _cache.SetKnowledge(Knowledge);
            await _cache.SaveAsync().ConfigureAwait(false);
            Status = $"Knowledge is up to date · {Knowledge.Overview.TotalRecords:N0} records";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Status = Knowledge is not null
                ? Knowledge.IsLibraryFallback
                    ? "Library coverage is ready. Update Radio Vault Server for research evidence and triage."
                    : "The live Knowledge update failed · showing the latest saved snapshot."
                : exception is HttpRequestException
                    { StatusCode: System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.MethodNotAllowed }
                    ? "The paired server does not expose the Knowledge service. Update Radio Vault Server, then pull down to retry."
                    : "Knowledge could not be loaded: " + exception.Message;
        }
        return Knowledge;
    }

    public async Task<MobileKnowledgeCoverageResult> LoadCoverageAsync(
        int collectionId,
        bool isPaired,
        bool isLiveConnected,
        CancellationToken cancellationToken = default)
    {
        if (!isPaired || !isLiveConnected)
        {
            var saved = BuildLibraryCoverage(collectionId);
            return new MobileKnowledgeCoverageResult(
                saved,
                saved is null
                    ? "No saved coverage is available for that show."
                    : $"Saved Library coverage loaded for {saved.ShowName}.");
        }
        try
        {
            var coverage = await _transport
                .GetCoverageAsync(collectionId, cancellationToken)
                .ConfigureAwait(false);
            return new MobileKnowledgeCoverageResult(
                coverage,
                coverage is null
                    ? "No dated coverage is available for that show."
                    : $"Coverage loaded for {coverage.ShowName}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var saved = BuildLibraryCoverage(collectionId);
            return new MobileKnowledgeCoverageResult(
                saved,
                saved is null
                    ? "Coverage could not be loaded: " + exception.Message
                    : $"Live Knowledge is unavailable · showing saved Library coverage for {saved.ShowName}.");
        }
    }

    public async Task<bool> ResolveDateReviewAsync(
        MobileKnowledgeDateReview review,
        int action,
        bool isPaired,
        bool isLiveConnected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);
        if (!isPaired || !isLiveConnected) return false;
        try
        {
            Status = action switch
            {
                0 => "Accepting the suggested date…",
                1 => "Keeping the current Library date…",
                2 => "Ignoring this suggestion…",
                6 => "Reopening the date suggestion…",
                _ => "Saving the Knowledge decision…"
            };
            await _transport.ResolveDateReviewAsync(
                review.ResearchId,
                action,
                action == 0 ? review.ProposedDate : null,
                cancellationToken).ConfigureAwait(false);
            await LoadAsync(isPaired, isLiveConnected, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Status = "Knowledge decision failed: " + exception.Message;
            return false;
        }
    }

    internal MobileKnowledgeSnapshot? BuildLibrarySnapshot()
    {
        var snapshot = _cache.Snapshot;
        var broadcasts = snapshot.Broadcasts;
        if (broadcasts.Count == 0) return null;
        var dated = broadcasts.Where(value => value.AirDate.HasValue).ToArray();
        var collections = broadcasts
            .GroupBy(value => new { value.CollectionId, value.CollectionName })
            .Select(group => new MobileKnowledgeCollection(
                group.Key.CollectionId,
                group.Key.CollectionName,
                group.Count()))
            .GroupBy(value => NormalizeCollectionName(value.Name))
            .Where(group => group.Key.Length > 0)
            .Select(group =>
            {
                var distinct = group
                    .GroupBy(value => value.CollectionId)
                    .Select(value => value.First())
                    .ToArray();
                var display = distinct
                    .OrderByDescending(value => value.RecordCount)
                    .ThenBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase)
                    .First();
                return new MobileKnowledgeCollection(
                    display.CollectionId,
                    display.Name.Trim(),
                    distinct.Sum(value => value.RecordCount));
            })
            .OrderBy(value => value.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var overview = new MobileKnowledgeOverview(
            broadcasts.Count,
            broadcasts.Count,
            0,
            broadcasts.Count(value => value.NeedsAttention),
            0,
            0,
            broadcasts.Count(value => !string.IsNullOrWhiteSpace(value.Description)),
            0,
            0,
            0,
            null,
            dated.Length == 0 ? null : dated.Min(value => value.AirDate),
            dated.Length == 0 ? null : dated.Max(value => value.AirDate));
        return new MobileKnowledgeSnapshot(overview, collections, [], snapshot.UpdatedAt, true);
    }

    internal MobileKnowledgeCoverage? BuildLibraryCoverage(int collectionId)
    {
        var collection = Knowledge?.Collections.FirstOrDefault(value => value.CollectionId == collectionId);
        var cachedCollections = _cache.Snapshot.Broadcasts
            .GroupBy(value => new { value.CollectionId, value.CollectionName })
            .Select(group => new MobileKnowledgeCollection(
                group.Key.CollectionId,
                group.Key.CollectionName,
                group.Count()));
        var collectionName = collection?.Name ??
                             cachedCollections.FirstOrDefault(value => value.CollectionId == collectionId)?.Name;
        if (string.IsNullOrWhiteSpace(collectionName)) return null;
        var key = NormalizeCollectionName(collectionName);
        var broadcasts = _cache.Snapshot.Broadcasts
            .Where(value => value.AirDate.HasValue && NormalizeCollectionName(value.CollectionName) == key)
            .ToArray();
        if (broadcasts.Length == 0) return null;
        var first = broadcasts.Min(value => value.AirDate!.Value);
        var last = broadcasts.Max(value => value.AirDate!.Value);
        var byDate = broadcasts
            .GroupBy(value => value.AirDate!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var days = new List<MobileKnowledgeCoverageDay>();
        for (var date = first; date <= last; date = date.AddDays(1))
        {
            byDate.TryGetValue(date, out var dated);
            dated ??= [];
            var score = dated.Length == 0 ? 0 : (int)Math.Round(dated.Average(MetadataScore));
            days.Add(new MobileKnowledgeCoverageDay(
                date,
                date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
                dated.Length > 0,
                false,
                false,
                dated.Length,
                score,
                dated.Length == 0 ? "No saved broadcast" : score >= 75 ? string.Empty : "More metadata can be added",
                dated.FirstOrDefault()?.RepresentativeEpisodeId,
                null));
        }
        return new MobileKnowledgeCoverage(collectionId, collectionName, first, last, days);
    }

    private static double MetadataScore(WebClientLibraryBroadcastSummary value)
    {
        var score = 25d;
        if (!string.IsNullOrWhiteSpace(value.Title)) score += 15;
        if (!string.IsNullOrWhiteSpace(value.Description)) score += 25;
        if (!string.IsNullOrWhiteSpace(value.ArtworkPath)) score += 15;
        if (value.RecordingCount > 0) score += 10;
        if (value.SegmentCount > 0) score += 10;
        return Math.Min(100, score);
    }

    private static string NormalizeCollectionName(string? value)
        => new((value ?? string.Empty).Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
}

internal sealed record MobileKnowledgeCoverageResult(
    MobileKnowledgeCoverage? Coverage,
    string Status);

internal interface IMobileKnowledgeTransport
{
    Task<MobileKnowledgeOverview> GetOverviewAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MobileKnowledgeCollection>> GetCollectionsAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MobileKnowledgeDateReview>> GetDateReviewsAsync(CancellationToken cancellationToken = default);
    Task<MobileKnowledgeCoverage?> GetCoverageAsync(
        int collectionId,
        CancellationToken cancellationToken = default);
    Task ResolveDateReviewAsync(
        long researchId,
        int action,
        DateOnly? selectedDate,
        CancellationToken cancellationToken = default);
}

internal sealed class MobileKnowledgeTransport(MobileServerClient server) : IMobileKnowledgeTransport
{
    private readonly MobileServerClient _server = server ?? throw new ArgumentNullException(nameof(server));

    public Task<MobileKnowledgeOverview> GetOverviewAsync(CancellationToken cancellationToken = default)
        => _server.GetKnowledgeOverviewAsync(cancellationToken);
    public Task<IReadOnlyList<MobileKnowledgeCollection>> GetCollectionsAsync(CancellationToken cancellationToken = default)
        => _server.GetKnowledgeCollectionsAsync(cancellationToken);
    public Task<IReadOnlyList<MobileKnowledgeDateReview>> GetDateReviewsAsync(CancellationToken cancellationToken = default)
        => _server.GetKnowledgeDateReviewsAsync(cancellationToken);
    public Task<MobileKnowledgeCoverage?> GetCoverageAsync(
        int collectionId,
        CancellationToken cancellationToken = default)
        => _server.GetKnowledgeCoverageAsync(collectionId, cancellationToken);
    public Task ResolveDateReviewAsync(
        long researchId,
        int action,
        DateOnly? selectedDate,
        CancellationToken cancellationToken = default)
        => _server.ResolveKnowledgeDateReviewAsync(researchId, action, selectedDate, cancellationToken);
}
