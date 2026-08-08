namespace TheRadioVault.Application.Models;

public sealed record FileSelectionRequest(
    string? Title = null,
    string? Filter = null,
    string? InitialDirectory = null,
    string? DefaultExtension = null,
    string? SuggestedFileName = null,
    bool CheckFileExists = true,
    bool AllowCreateFolder = true);
