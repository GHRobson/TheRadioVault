using System.Text.Json;

namespace TheRadioVault.Research.Services;

/// <summary>
/// Protects reversible research-quality repairs from overwriting edits or imports
/// that happened after the repair was applied.
/// </summary>
public static class ResearchRepairGuard
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static bool CanUndo(string currentSnapshotJson, string appliedSnapshotJson)
    {
        if (string.IsNullOrWhiteSpace(currentSnapshotJson) || string.IsNullOrWhiteSpace(appliedSnapshotJson))
            return false;

        try
        {
            using var current = JsonDocument.Parse(currentSnapshotJson);
            using var applied = JsonDocument.Parse(appliedSnapshotJson);
            var currentCanonical = JsonSerializer.Serialize(current.RootElement, JsonOptions);
            var appliedCanonical = JsonSerializer.Serialize(applied.RootElement, JsonOptions);
            return string.Equals(currentCanonical, appliedCanonical, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
