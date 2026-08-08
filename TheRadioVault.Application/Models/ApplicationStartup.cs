namespace TheRadioVault.Application.Models;

public sealed record ApplicationStartupRequest(
    bool ForceLocalLibrary,
    bool UseRemoteLibraryOnStartup,
    bool HasSavedServer);

public sealed record ApplicationStartupPlan(
    ApplicationSessionMode Mode,
    string Reason)
{
    public bool IsRemoteClient => Mode == ApplicationSessionMode.RemoteClient;
}
