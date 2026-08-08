using TheRadioVault.Services.Models;

namespace TheRadioVault.Services.Contracts;

public interface IResearchPackTransferService
{
    bool IsAvailable { get; }
    bool IsRemoteOwned { get; }
    Task<ResearchPackPreviewSummary> PreviewImportAsync(
        string filePath,
        IProgress<ResearchPackTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task<ResearchPackApplySummary> ApplyImportAsync(
        IProgress<ResearchPackTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
    Task CancelImportAsync(CancellationToken cancellationToken = default);
    Task<ResearchPackExportSummary> ExportAsync(CancellationToken cancellationToken = default);
}
