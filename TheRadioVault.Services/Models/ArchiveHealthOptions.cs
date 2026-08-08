namespace TheRadioVault.Services.Models;

/// <summary>
/// Controls how Archive Health interprets optional storage conditions.
/// </summary>
public sealed record ArchiveHealthOptions(bool IncludeCloudOnlyInHealth = false);
