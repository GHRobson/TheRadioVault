namespace TheRadioVault.Core.Abstractions;

public interface IFilePickerService
{
    Task<string?> PickFolderAsync(CancellationToken cancellationToken = default);
}

public interface IPlatformPathService
{
    string AppDataDirectory { get; }
    string CacheDirectory { get; }
}

public interface ISystemThemeService
{
    bool IsDarkTheme { get; }
}

public interface IExternalFileService
{
    Task RevealInFileManagerAsync(string path, CancellationToken cancellationToken = default);
}

public interface ISettingsStore
{
    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);
    Task SetAsync(string key, string? value, CancellationToken cancellationToken = default);
}

public interface ILibraryRepository
{
    Task<IReadOnlyList<Domain.ArchiveEpisode>> GetArchiveEpisodesAsync(int? collectionId = null, CancellationToken cancellationToken = default);
}
