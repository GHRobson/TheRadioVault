namespace TheRadioVault.Presentation.ViewModels;

public sealed record TranscriptionBatchCollectionOption(int? CollectionId, string Name, int BroadcastCount)
{
    public string Display => CollectionId.HasValue ? $"{Name} · {BroadcastCount:N0}" : Name;
}

public sealed record TranscriptionBatchYearOption(int? Year)
{
    public string Display => Year?.ToString() ?? "All years";
}
