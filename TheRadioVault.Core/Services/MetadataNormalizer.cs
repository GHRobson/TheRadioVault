namespace TheRadioVault.Core.Services;

public static class MetadataNormalizer
{
    public static string? NormalizeCollection(string? value)
        => KnownShowCatalog.Normalize(value);

    public static string? NormalizeStation(string? value) => StationDictionary.Normalize(value);
}
