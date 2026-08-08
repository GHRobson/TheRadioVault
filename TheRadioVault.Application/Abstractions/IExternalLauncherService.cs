using TheRadioVault.Application.Models;

namespace TheRadioVault.Application.Abstractions;

public interface IExternalLauncherService
{
    Task LaunchAsync(ExternalLaunchRequest request, CancellationToken cancellationToken = default);
}
