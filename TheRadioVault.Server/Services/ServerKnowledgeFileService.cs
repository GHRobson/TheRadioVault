using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace TheRadioVault.Server.Services;

public sealed class ServerKnowledgeFileService
{
    private static readonly FilePickerFileType KnowledgeDatabaseType = new("Radio Vault Knowledge Database")
    {
        Patterns = ["*.trvknowledge"],
        MimeTypes = ["application/vnd.radiovault.knowledge+sqlite3"]
    };
    private static readonly FilePickerFileType DiagnosticsType = new("Radio Vault diagnostics")
    {
        Patterns = ["*.trvdiag.json"],
        MimeTypes = ["application/json"]
    };
    private static readonly FilePickerFileType ReconciliationReportType = new("Radio Vault reconciliation report")
    {
        Patterns = ["*.trvreconcile.json"],
        MimeTypes = ["application/json"]
    };
    private static readonly FilePickerFileType DateAuthorityEvidenceType = new("Radio Vault date-authority evidence")
    {
        Patterns = ["*.trvdateevidence.json"],
        MimeTypes = ["application/json"]
    };

    private readonly Window _owner;

    public ServerKnowledgeFileService(Window owner)
        => _owner = owner ?? throw new ArgumentNullException(nameof(owner));

    public async Task<string?> PickImportAsync(CancellationToken cancellationToken = default)
    {
        var files = await _owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a Radio Vault Knowledge Database",
            AllowMultiple = false,
            FileTypeFilter = [KnowledgeDatabaseType]
        }).WaitAsync(cancellationToken).ConfigureAwait(true);
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickExportAsync(
        string suggestedFileName,
        CancellationToken cancellationToken = default)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export a Radio Vault Knowledge Database",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "trvknowledge",
            FileTypeChoices = [KnowledgeDatabaseType],
            ShowOverwritePrompt = true
        }).WaitAsync(cancellationToken).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickDiagnosticsExportAsync(
        string suggestedFileName,
        CancellationToken cancellationToken = default)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export privacy-safe Radio Vault diagnostics",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "json",
            FileTypeChoices = [DiagnosticsType],
            ShowOverwritePrompt = true
        }).WaitAsync(cancellationToken).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickReconciliationReportExportAsync(
        string suggestedFileName,
        CancellationToken cancellationToken = default)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export a Radio Vault archive reconciliation report",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "trvreconcile.json",
            FileTypeChoices = [ReconciliationReportType],
            ShowOverwritePrompt = true
        }).WaitAsync(cancellationToken).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickDateAuthorityEvidenceExportAsync(
        string suggestedFileName,
        CancellationToken cancellationToken = default)
    {
        var file = await _owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export unresolved-date authority evidence",
            SuggestedFileName = suggestedFileName,
            DefaultExtension = "trvdateevidence.json",
            FileTypeChoices = [DateAuthorityEvidenceType],
            ShowOverwritePrompt = true
        }).WaitAsync(cancellationToken).ConfigureAwait(true);
        return file?.TryGetLocalPath();
    }
}
