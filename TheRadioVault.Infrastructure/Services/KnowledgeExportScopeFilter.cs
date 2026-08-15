using TheRadioVault.Core.Services;
using TheRadioVault.Models;

namespace TheRadioVault.Services;

public static class KnowledgeExportScopeFilter
{
    public static TrvKnowledgePack Apply(TrvKnowledgePack pack, KnowledgeExportScope scope)
    {
        ArgumentNullException.ThrowIfNull(pack);
        pack.Broadcasts ??= new();
        pack.MissingBroadcasts ??= new();
        pack.Transcripts ??= new();

        pack.Manifest.ExportScope = scope.ToWireValue();
        pack.Manifest.Purpose = scope switch
        {
            KnowledgeExportScope.UndatedBroadcasts => "Focused date research for broadcasts without an established broadcast date",
            KnowledgeExportScope.MissingTopicsOrSummaries => "Focused research for broadcasts missing topics or a summary",
            _ => "Complete Radio Vault knowledge database"
        };

        if (scope == KnowledgeExportScope.Complete)
        {
            UpdateCounts(pack);
            return pack;
        }

        var allBroadcasts = pack.Broadcasts.Concat(pack.MissingBroadcasts).ToArray();
        var uniqueFallbackIdentities = allBroadcasts
            .GroupBy(FallbackIdentity, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        pack.Broadcasts = pack.Broadcasts.Where(item => IsIncluded(item, scope)).ToList();
        pack.MissingBroadcasts = pack.MissingBroadcasts.Where(item => IsIncluded(item, scope)).ToList();

        var included = pack.Broadcasts.Concat(pack.MissingBroadcasts).ToArray();
        var broadcastIds = included
            .Select(item => Clean(item.BroadcastId))
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var fallbackIdentities = included
            .Select(FallbackIdentity)
            .Where(uniqueFallbackIdentities.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        pack.Transcripts = pack.Transcripts
            .Where(transcript =>
                (!string.IsNullOrWhiteSpace(transcript.BroadcastId) && broadcastIds.Contains(Clean(transcript.BroadcastId))) ||
                (string.IsNullOrWhiteSpace(transcript.BroadcastId) && fallbackIdentities.Contains(FallbackIdentity(transcript))))
            .ToList();
        pack.Wiki = null;
        UpdateCounts(pack);
        return pack;
    }

    private static bool IsIncluded(TrvPackBroadcast item, KnowledgeExportScope scope) => scope switch
    {
        KnowledgeExportScope.UndatedBroadcasts => string.IsNullOrWhiteSpace(item.BroadcastDate),
        KnowledgeExportScope.MissingTopicsOrSummaries =>
            string.IsNullOrWhiteSpace(item.Research?.Summary) || item.Research?.Topics is not { Count: > 0 },
        _ => true
    };

    private static void UpdateCounts(TrvKnowledgePack pack)
    {
        pack.Manifest.BroadcastCount = pack.Broadcasts.Count;
        pack.Manifest.MissingBroadcastCount = pack.MissingBroadcasts.Count;
        pack.Manifest.TranscriptCount = pack.Transcripts.Count;
        pack.Manifest.WikiPageCount = pack.Wiki?.Pages.Count ?? 0;
        pack.Manifest.WikiImageCount = pack.Wiki?.Images.Count ?? 0;
        pack.Manifest.WikiTimelineEventCount = pack.Wiki?.TimelineEvents.Count ?? 0;
    }

    private static string FallbackIdentity(TrvPackBroadcast item)
        => $"{Clean(item.Show)}|{Clean(item.BroadcastDate)}|{Math.Max(1, item.PartNumber)}";

    private static string FallbackIdentity(TrvPackTranscript item)
        => $"{Clean(item.Show)}|{Clean(item.BroadcastDate)}|{Math.Max(1, item.PartNumber)}";

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}
