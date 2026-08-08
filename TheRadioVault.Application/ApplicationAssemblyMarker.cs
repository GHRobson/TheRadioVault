namespace TheRadioVault.Application;

/// <summary>
/// Stable marker for the platform-neutral application boundary.
/// UI projects may reference this assembly; this assembly must never reference a UI toolkit.
/// </summary>
public static class ApplicationAssemblyMarker
{
    public static Type AssemblyType => typeof(ApplicationAssemblyMarker);
}
