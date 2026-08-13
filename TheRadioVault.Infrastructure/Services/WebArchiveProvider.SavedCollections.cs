using TheRadioVault.Services.Models;
using TheRadioVault.Services.Services;
using TheRadioVault.Web.Models;

namespace TheRadioVault.Services;

internal sealed partial class WebArchiveProvider
{
    public IReadOnlyList<WebSavedCollectionSummary> GetSavedCollections()
        => CreateSavedCollectionService()
            .GetAllAsync()
            .GetAwaiter()
            .GetResult()
            .Select(Map)
            .ToArray();

    public WebSavedCollectionDetails? GetSavedCollection(long collectionId)
    {
        var value = CreateSavedCollectionService().GetAsync(collectionId).GetAwaiter().GetResult();
        return value is null ? null : Map(value);
    }

    public WebSavedCollectionMutationResult CreateSavedCollection(WebSavedCollectionCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.TryParse<SavedCollectionKind>(request.Kind, true, out var kind))
            return InvalidSavedCollection("Saved collection kind must be Manual or Smart.");
        if (request.FromQueue && kind != SavedCollectionKind.Manual)
            return InvalidSavedCollection("Only manual playlists can be created from Up Next.");

        return ExecuteSavedCollectionMutation(null, async () =>
        {
            IReadOnlyList<long>? episodeIds = request.EpisodeIds;
            if (request.FromQueue)
            {
                episodeIds = (await new QueueService(_database.PlatformDatabase).GetAsync().ConfigureAwait(false))
                    .Select(item => item.BroadcastId)
                    .ToArray();
            }
            return await CreateSavedCollectionService()
                .CreateAsync(request.Name, kind, Map(request.Rule), episodeIds)
                .ConfigureAwait(false);
        }, "created");
    }

    public WebSavedCollectionMutationResult UpdateSavedCollection(long collectionId, WebSavedCollectionUpdateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteSavedCollectionMutation(collectionId,
            () => CreateSavedCollectionService().UpdateAsync(collectionId, request.Name, Map(request.Rule), request.ExpectedRevision),
            "updated");
    }

    public WebSavedCollectionMutationResult AddSavedCollectionItem(long collectionId, WebSavedCollectionItemMutation request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteSavedCollectionMutation(collectionId,
            () => CreateSavedCollectionService().AddAsync(collectionId, request.EpisodeId, request.ExpectedRevision),
            "item-added");
    }

    public WebSavedCollectionMutationResult RemoveSavedCollectionItem(long collectionId, WebSavedCollectionItemMutation request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteSavedCollectionMutation(collectionId,
            () => CreateSavedCollectionService().RemoveAsync(collectionId, request.EpisodeId, request.ExpectedRevision),
            "item-removed");
    }

    public WebSavedCollectionMutationResult MoveSavedCollectionItem(long collectionId, WebSavedCollectionItemMutation request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.TargetIndex.HasValue) return InvalidSavedCollection("A target index is required when moving a playlist item.");
        return ExecuteSavedCollectionMutation(collectionId,
            () => CreateSavedCollectionService().MoveAsync(collectionId, request.EpisodeId, request.TargetIndex.Value, request.ExpectedRevision),
            "item-moved");
    }

    public WebSavedCollectionMutationResult DeleteSavedCollection(long collectionId, WebSavedCollectionDeleteRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            CreateSavedCollectionService().DeleteAsync(collectionId, request.ExpectedRevision).GetAwaiter().GetResult();
            AddChange("saved-collection", null, $"deleted:{collectionId}", DateTimeOffset.UtcNow);
            return new WebSavedCollectionMutationResult(true, false, false, "Saved collection deleted.", null);
        }
        catch (SavedCollectionConflictException exception)
        {
            return ConflictSavedCollection(exception);
        }
        catch (KeyNotFoundException exception)
        {
            return new WebSavedCollectionMutationResult(false, false, true, exception.Message, null);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return InvalidSavedCollection(exception.Message);
        }
    }

    private SavedCollectionService CreateSavedCollectionService() => new(_database.PlatformDatabase);

    private WebSavedCollectionMutationResult ExecuteSavedCollectionMutation(
        long? collectionId,
        Func<Task<SavedCollectionDetails>> operation,
        string reason)
    {
        try
        {
            var value = operation().GetAwaiter().GetResult();
            AddChange("saved-collection", null, $"{reason}:{value.Summary.Id}:{value.Summary.Revision}", DateTimeOffset.UtcNow);
            return new WebSavedCollectionMutationResult(true, false, false, "Saved collection updated.", Map(value), value.Summary.Revision);
        }
        catch (SavedCollectionConflictException exception)
        {
            return ConflictSavedCollection(exception);
        }
        catch (KeyNotFoundException exception)
        {
            return new WebSavedCollectionMutationResult(false, false, true, exception.Message, null);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or InvalidDataException)
        {
            return InvalidSavedCollection(exception.Message);
        }
    }

    private WebSavedCollectionMutationResult ConflictSavedCollection(SavedCollectionConflictException exception)
    {
        var current = GetSavedCollection(exception.CollectionId);
        return new WebSavedCollectionMutationResult(
            false, true, false, exception.Message, current, exception.ActualRevision);
    }

    private static WebSavedCollectionMutationResult InvalidSavedCollection(string message)
        => new(false, false, false, message, null);

    private static WebSavedCollectionSummary Map(SavedCollectionSummary value)
        => new(value.Id, value.Name, value.Kind.ToString(), value.ItemCount, value.Revision, value.CreatedAt, value.UpdatedAt);

    private static WebSavedCollectionDetails Map(SavedCollectionDetails value)
        => new(Map(value.Summary), Map(value.Rule), value.Broadcasts.Select(Map).ToArray());

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
        if (!Enum.TryParse<LibraryListeningFilter>(value.Filter, true, out var filter))
            throw new ArgumentException("Saved collection filter is invalid.", nameof(value));
        if (!Enum.TryParse<LibrarySearchScope>(value.SearchScope, true, out var scope))
            throw new ArgumentException("Saved collection search scope is invalid.", nameof(value));
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
}
