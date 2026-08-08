using TheRadioVault.Data.Database;
using TheRadioVault.Services.Contracts;

namespace TheRadioVault.Services.Services;

public sealed class LibraryActionService : ILibraryActionService
{
    private readonly ArchiveService _archive;
    private readonly CanonicalLibraryQueryService _canonical;

    public LibraryActionService(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _archive = new ArchiveService(database);
        _canonical = new CanonicalLibraryQueryService(database);
    }

    public Task SetFavouriteAsync(
        long representativeEpisodeId,
        bool favourite,
        CancellationToken cancellationToken = default)
    {
        if (representativeEpisodeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(representativeEpisodeId));

        var ids = _canonical.ExpandStateEpisodeIds(representativeEpisodeId);
        return _archive.SetFavouriteAsync(ids, favourite, cancellationToken);
    }

    public Task SetPlayedAsync(
        long representativeEpisodeId,
        bool played,
        CancellationToken cancellationToken = default)
    {
        if (representativeEpisodeId <= 0)
            throw new ArgumentOutOfRangeException(nameof(representativeEpisodeId));

        var ids = _canonical.ExpandStateEpisodeIds(representativeEpisodeId);
        return _archive.SetPlayedAsync(ids, played, cancellationToken);
    }
}
