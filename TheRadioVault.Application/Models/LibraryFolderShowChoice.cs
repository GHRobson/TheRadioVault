namespace TheRadioVault.Application.Models;

/// <summary>
/// One assignment offered after the user chooses a local archive folder.
/// A null collection id deliberately means automatic per-file show detection.
/// </summary>
public sealed record LibraryFolderShowChoice(
    int? CollectionId,
    string Name,
    string Description)
{
    public override string ToString() => Name;
}
