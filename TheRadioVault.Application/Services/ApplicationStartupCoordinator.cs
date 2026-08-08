using TheRadioVault.Application.Models;

namespace TheRadioVault.Application.Services;

public sealed class ApplicationStartupCoordinator
{
    public ApplicationStartupPlan CreatePlan(ApplicationStartupRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.ForceLocalLibrary)
        {
            return new ApplicationStartupPlan(
                ApplicationSessionMode.LocalLibrary,
                "The launch request explicitly selected the local library.");
        }

        if (request.UseRemoteLibraryOnStartup && request.HasSavedServer)
        {
            return new ApplicationStartupPlan(
                ApplicationSessionMode.RemoteClient,
                "Saved Connected Access preferences selected the server library.");
        }

        if (request.UseRemoteLibraryOnStartup)
        {
            return new ApplicationStartupPlan(
                ApplicationSessionMode.LocalLibrary,
                "The server library was requested, but no saved server connection is available.");
        }

        return new ApplicationStartupPlan(
            ApplicationSessionMode.LocalLibrary,
            "Saved preferences selected this installation's local library.");
    }
}
