using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

/// <summary>
/// Publishes a clock-driven archive station. Reading or listening to this
/// schedule never writes ordinary Library playback state.
/// </summary>
public interface ILiveRadioService
{
    Task<LiveRadioSnapshot> GetSnapshotAsync(
        DateTimeOffset? at = null,
        CancellationToken cancellationToken = default);
}
