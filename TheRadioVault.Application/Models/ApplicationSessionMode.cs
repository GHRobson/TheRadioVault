namespace TheRadioVault.Application.Models;

public enum ApplicationSessionMode
{
    LocalLibrary,
    RemoteClient
}

public static class ApplicationSessionModeExtensions
{
    public static string ToDiagnosticName(this ApplicationSessionMode mode) => mode switch
    {
        ApplicationSessionMode.RemoteClient => "remote-client",
        _ => "local-library"
    };
}
