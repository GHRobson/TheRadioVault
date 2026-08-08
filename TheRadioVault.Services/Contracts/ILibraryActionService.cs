namespace TheRadioVault.Services.Contracts;

/// <summary>
/// Canonical write boundary for presentation-shell library actions. The
/// service expands a representative episode to every member of the canonical
/// broadcast before applying state changes.
/// </summary>
public interface ILibraryActionService
{
    Task SetFavouriteAsync(
        long representativeEpisodeId,
        bool favourite,
        CancellationToken cancellationToken = default);
    Task SetPlayedAsync(
        long representativeEpisodeId,
        bool played,
        CancellationToken cancellationToken = default);
}
