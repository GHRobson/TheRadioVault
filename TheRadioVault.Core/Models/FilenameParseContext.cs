namespace TheRadioVault.Core.Models;

public sealed record FilenameParseContext(bool ShouldIgnoreLeadingSequence, string Reasoning, string? AssignedCollectionName = null)
{
    public static FilenameParseContext None { get; } = new(false, "No reliable folder-level sequence pattern was detected.");
}
