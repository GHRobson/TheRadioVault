using TheRadioVault.Services.Contracts;
using TheRadioVault.Services.Models;
using TheRadioVault.Web.Contracts;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

public sealed class LoopbackSavedCollectionService : ISavedCollectionService
{
    private readonly LoopbackServerClient _connection;

    public LoopbackSavedCollectionService(LoopbackServerClient connection)
        => _connection = connection ?? throw new ArgumentNullException(nameof(connection));

    public async Task<IReadOnlyList<SavedCollectionSummary>> GetAllAsync(CancellationToken cancellationToken = default)
        => (await _connection.SendJsonAsync<ListEnvelope>(
            HttpMethod.Get,
            WebApiRoutes.ClientSavedCollections,
            cancellationToken: cancellationToken).ConfigureAwait(false))
            .Collections.Select(Map).ToArray();

    public async Task<SavedCollectionDetails?> GetAsync(long collectionId, CancellationToken cancellationToken = default)
    {
        if (collectionId <= 0) return null;
        var envelope = await _connection.GetJsonOrNullAsync<DetailsEnvelope>(
            WebApiRoutes.ClientSavedCollection(collectionId), cancellationToken).ConfigureAwait(false);
        return envelope is null ? null : Map(envelope.Collection);
    }

    public Task<SavedCollectionDetails> CreateAsync(
        string name,
        SavedCollectionKind kind,
        SavedCollectionRule? rule = null,
        IReadOnlyList<long>? episodeIds = null,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            WebApiRoutes.ClientSavedCollections,
            new WebSavedCollectionCreateRequest(name, kind.ToString(), Map(rule), false, episodeIds),
            collectionId: null,
            cancellationToken);

    public Task<SavedCollectionDetails> UpdateAsync(
        long collectionId,
        string name,
        SavedCollectionRule? rule,
        long expectedRevision,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            WebApiRoutes.ClientSavedCollectionUpdate(collectionId),
            new WebSavedCollectionUpdateRequest(name, Map(rule), expectedRevision),
            collectionId,
            cancellationToken);

    public Task<SavedCollectionDetails> AddAsync(
        long collectionId,
        long episodeId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            WebApiRoutes.ClientSavedCollectionAdd(collectionId),
            new WebSavedCollectionItemMutation(episodeId, expectedRevision),
            collectionId,
            cancellationToken);

    public Task<SavedCollectionDetails> RemoveAsync(
        long collectionId,
        long episodeId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            WebApiRoutes.ClientSavedCollectionRemove(collectionId),
            new WebSavedCollectionItemMutation(episodeId, expectedRevision),
            collectionId,
            cancellationToken);

    public Task<SavedCollectionDetails> MoveAsync(
        long collectionId,
        long episodeId,
        int targetIndex,
        long expectedRevision,
        CancellationToken cancellationToken = default)
        => MutateAsync(
            WebApiRoutes.ClientSavedCollectionMove(collectionId),
            new WebSavedCollectionItemMutation(episodeId, expectedRevision, targetIndex),
            collectionId,
            cancellationToken);

    public async Task DeleteAsync(
        long collectionId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var envelope = await _connection.SendJsonAsync<MutationEnvelope>(
            HttpMethod.Post,
            WebApiRoutes.ClientSavedCollectionDelete(collectionId),
            new WebSavedCollectionDeleteRequest(expectedRevision),
            allowConflict: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(envelope.Result, collectionId, expectedRevision);
    }

    private async Task<SavedCollectionDetails> MutateAsync(
        string route,
        object mutation,
        long? collectionId,
        CancellationToken cancellationToken)
    {
        var envelope = await _connection.SendJsonAsync<MutationEnvelope>(
            HttpMethod.Post,
            route,
            mutation,
            allowConflict: true,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        ThrowIfFailed(envelope.Result, collectionId, ExpectedRevision(mutation));
        return envelope.Result.Collection is { } collection
            ? Map(collection)
            : throw new InvalidOperationException("Radio Vault Server did not return the saved collection.");
    }

    private static void ThrowIfFailed(WebSavedCollectionMutationResult result, long? collectionId, long expectedRevision)
    {
        if (result.Changed) return;
        if (result.Conflict && collectionId.HasValue)
            throw new SavedCollectionConflictException(collectionId.Value, expectedRevision, result.CurrentRevision ?? 0);
        if (result.NotFound) throw new KeyNotFoundException(result.Message);
        throw new InvalidOperationException(result.Message);
    }

    private static long ExpectedRevision(object mutation) => mutation switch
    {
        WebSavedCollectionUpdateRequest value => value.ExpectedRevision,
        WebSavedCollectionItemMutation value => value.ExpectedRevision,
        WebSavedCollectionDeleteRequest value => value.ExpectedRevision,
        _ => 0
    };

    private static SavedCollectionSummary Map(WebSavedCollectionSummary value)
        => new(
            value.Id,
            value.Name,
            Enum.TryParse<SavedCollectionKind>(value.Kind, true, out var kind) ? kind : SavedCollectionKind.Manual,
            value.ItemCount,
            value.Revision,
            value.CreatedAt,
            value.UpdatedAt);

    private static SavedCollectionDetails Map(WebSavedCollectionDetails value)
        => new(
            Map(value.Summary),
            Map(value.Rule),
            value.Broadcasts.Select(LoopbackLibraryBrowseService.Map).ToArray());

    private static WebSavedCollectionRule? Map(SavedCollectionRule? value)
        => value is null ? null : new WebSavedCollectionRule(
            value.SearchText,
            value.CollectionId,
            value.Filter.ToString(),
            value.Year,
            value.Month,
            value.SearchScope.ToString(),
            value.HasTranscript,
            value.HideCompleted,
            value.NewestFirst,
            value.Limit);

    private static SavedCollectionRule? Map(WebSavedCollectionRule? value)
    {
        if (value is null) return null;
        Enum.TryParse<LibraryListeningFilter>(value.Filter, true, out var filter);
        Enum.TryParse<LibrarySearchScope>(value.SearchScope, true, out var scope);
        return new SavedCollectionRule(
            value.SearchText,
            value.CollectionId,
            filter,
            value.Year,
            value.Month,
            scope,
            value.HasTranscript,
            value.HideCompleted,
            value.NewestFirst,
            value.Limit);
    }

    private sealed record ListEnvelope(IReadOnlyList<WebSavedCollectionSummary> Collections);
    private sealed record DetailsEnvelope(WebSavedCollectionDetails Collection);
    private sealed record MutationEnvelope(WebSavedCollectionMutationResult Result);
}
