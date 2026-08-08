using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TheRadioVault.Core.Services;
using TheRadioVault.Models;

namespace TheRadioVault.Services;

/// <summary>
/// Pure reconciliation policy for v0.26. Database writes remain in DatabaseService
/// (or a future ResearchLibraryRepository), which makes these rules easy to test.
/// </summary>
public static class ResearchReconciliationRules
{
    public const int StrongMatchThreshold = 90;
    public const int ReviewMatchThreshold = 65;

    public static ResearchMatchCandidate? ScoreMatch(
        ResearchBroadcastRecord research,
        EpisodeResearchSnapshot episode)
    {
        if (research.Identity.CollectionId != episode.CollectionId) return null;

        var score = 0;
        var reasons = new List<string>();

        if (research.Identity.AirDate is not null && episode.AirDate is not null)
        {
            if (research.Identity.AirDate != episode.AirDate) return null;
            score += 60;
            reasons.Add("same show and exact broadcast date");
        }
        else
        {
            score += 10;
            reasons.Add("date incomplete");
        }

        if (research.Identity.PartNumber == Math.Max(1, episode.PartNumber))
        {
            score += 15;
            reasons.Add("same part number");
        }
        else if (research.Identity.PartNumber > 1 || episode.PartNumber > 1)
        {
            score -= 20;
            reasons.Add("part number differs");
        }

        if (BroadcastSlotNormalizer.Equivalent(research.Identity.Slot, episode.Slot))
        {
            score += 15;
            reasons.Add("same broadcast slot");
        }
        else if (!string.IsNullOrWhiteSpace(research.Identity.Slot)
                 && !string.IsNullOrWhiteSpace(episode.Slot))
        {
            score -= 10;
            reasons.Add("broadcast slot differs");
        }

        if (!string.IsNullOrWhiteSpace(research.SourceBroadcastId)
            && !string.IsNullOrWhiteSpace(episode.BroadcastUid)
            && string.Equals(research.SourceBroadcastId.Trim(), episode.BroadcastUid.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            score += 60;
            reasons.Add("same stable broadcast identifier");
        }

        var filename = Path.GetFileNameWithoutExtension(episode.OriginalFilename);
        if (research.Aliases
            .Where(IsFilenameOrCaptureAlias)
            .Any(a => AliasMatches(a.AliasValue, filename)))
        {
            score += 20;
            reasons.Add("filename/capture alias matches");
        }

        if (!string.IsNullOrWhiteSpace(research.SourceBroadcastId)
            && filename.Contains(research.SourceBroadcastId, StringComparison.OrdinalIgnoreCase))
        {
            score += 15;
            reasons.Add("broadcast identifier appears in filename");
        }

        score = Math.Clamp(score, 0, 100);
        if (score < ReviewMatchThreshold) return null;

        return new ResearchMatchCandidate(
            research.Id,
            episode.EpisodeId,
            score,
            string.Join("; ", reasons),
            score >= StrongMatchThreshold);
    }

    public static IReadOnlyList<string> MergeNames(
        IEnumerable<string>? existing,
        IEnumerable<string>? incoming)
    {
        return (existing ?? Array.Empty<string>())
            .Concat(incoming ?? Array.Empty<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool ShouldTreatAsConfirmedMissing(int sourceCount, int confidence)
        => sourceCount >= 1 && confidence >= 85;

    public static bool ShouldTreatAsProbableMissing(int sourceCount, int confidence)
        => sourceCount >= 1 && confidence >= 60 && confidence < 85;

    private static bool HasMeaningfulDifference(
        ResearchBroadcastRecord research,
        EpisodeResearchSnapshot episode)
    {
        if (!string.IsNullOrWhiteSpace(research.Headline)
            && !string.IsNullOrWhiteSpace(episode.Headline)
            && !EquivalentText(research.Headline, episode.Headline)) return true;

        if (!string.IsNullOrWhiteSpace(research.Summary)
            && !string.IsNullOrWhiteSpace(episode.Summary)
            && !EquivalentText(research.Summary, episode.Summary)) return true;

        return false;
    }

    private static bool IsFilenameOrCaptureAlias(ResearchAliasRecord alias)
    {
        var type = alias.AliasType?.Trim().ToLowerInvariant() ?? string.Empty;
        return type is "filename" or "source_filename" or "media_filename" or "capture" or "capture_name";
    }

    private static bool AliasMatches(string alias, string filename)
    {
        if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(filename)) return false;
        var a = NormaliseLoose(alias);
        var f = NormaliseLoose(filename);
        return a == f || f.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EquivalentToken(string? left, string? right)
    {
        if (string.IsNullOrWhiteSpace(left) && string.IsNullOrWhiteSpace(right)) return true;
        return string.Equals(
            NormaliseLoose(left ?? string.Empty),
            NormaliseLoose(right ?? string.Empty),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool EquivalentText(string left, string right)
        => string.Equals(NormaliseLoose(left), NormaliseLoose(right), StringComparison.OrdinalIgnoreCase);

    private static string NormaliseLoose(string value)
        => new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
}
