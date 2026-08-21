namespace TheRadioVault.Core.Services;

public enum KnowledgeExportScope
{
    Complete,
    UndatedBroadcasts,
    MissingTopicsOrSummaries
}

public static class KnowledgeExportScopePolicy
{
    public static string ToWireValue(this KnowledgeExportScope scope) => scope switch
    {
        KnowledgeExportScope.UndatedBroadcasts => "undated",
        KnowledgeExportScope.MissingTopicsOrSummaries => "missing-topics-or-summaries",
        _ => "complete"
    };

    public static bool TryParse(string? value, out KnowledgeExportScope scope)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case null:
            case "":
            case "complete":
                scope = KnowledgeExportScope.Complete;
                return true;
            case "undated":
            case "undated-broadcasts":
                scope = KnowledgeExportScope.UndatedBroadcasts;
                return true;
            case "missing-topics-or-summaries":
            case "missing-research":
                scope = KnowledgeExportScope.MissingTopicsOrSummaries;
                return true;
            default:
                scope = KnowledgeExportScope.Complete;
                return false;
        }
    }

    public static string SuggestedFileName(this KnowledgeExportScope scope) => scope switch
    {
        KnowledgeExportScope.UndatedBroadcasts => "RadioVault-Undated-Broadcasts.trvknowledge",
        KnowledgeExportScope.MissingTopicsOrSummaries => "RadioVault-Missing-Topics-or-Summaries.trvknowledge",
        _ => "RadioVault-Archive-Knowledge.trvknowledge"
    };

    public static string DisplayName(this KnowledgeExportScope scope) => scope switch
    {
        KnowledgeExportScope.UndatedBroadcasts => "broadcasts without dates",
        KnowledgeExportScope.MissingTopicsOrSummaries => "broadcasts missing topics or summaries",
        _ => "the complete archive"
    };
}
