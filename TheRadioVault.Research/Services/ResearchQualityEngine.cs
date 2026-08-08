using TheRadioVault.Research.Models;

namespace TheRadioVault.Research.Services;

public sealed class ResearchQualityEngine
{
    private static readonly string[] GenericSummaryFragments =
    {
        "archive broadcast", "this episode features", "topics discussed include",
        "the hosts discuss a variety of topics", "a wide range of topics",
        "faction talk archive broadcast"
    };

    private static readonly HashSet<string> WeakTopics = new(StringComparer.Ordinal)
    {
        "misc", "miscellaneous", "general", "various topics", "other", "discussion"
    };

    public ResearchAuditResult Run(IEnumerable<ResearchAuditRecord> input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var records = input.ToList();
        var findings = new List<ResearchAuditFinding>();
        var repeatedSummaryGroups = records
            .Where(x => !string.IsNullOrWhiteSpace(x.Summary))
            .GroupBy(x => Normalise(x.Summary), StringComparer.Ordinal)
            .Where(x => x.Key.Length >= 40 && x.Count() >= 3)
            .ToArray();
        var repeatedSummaryKeys = repeatedSummaryGroups.Select(x => x.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var record in records)
        {
            if (IsWeakHeadline(record.Headline, record.Show))
            {
                var safeToClear = !string.IsNullOrWhiteSpace(record.Headline);
                Add(findings, record, "weak-headline", "Headline", ResearchAuditSeverity.Warning,
                    "Weak or generic headline",
                    string.IsNullOrWhiteSpace(record.Headline) ? "No useful headline is recorded." : $"“{record.Headline}” does not distinguish this broadcast.",
                    safeToClear ? "Clear the generic value so a specific headline can be researched later." : "Replace it with a concise, broadcast-specific headline.",
                    safeToClear ? ResearchAutoFixKind.ClearGenericHeadline : ResearchAutoFixKind.None,
                    safeToClear ? record.Headline : string.Empty);
            }

            if (IsGenericSummary(record.Summary) && !repeatedSummaryKeys.Contains(Normalise(record.Summary)))
                Add(findings, record, "generic-summary", "Summary", ResearchAuditSeverity.Warning,
                    "Generic summary",
                    $"The saved summary begins “{Preview(record.Summary)}” and uses stock wording rather than broadcast-specific information.",
                    "Keep it when the wording is intentionally accurate, or open the exact summary field to rewrite it.",
                    directDecisionKind: "generic-summary",
                    directDecisionSubject: record.Summary,
                    directDecisionOptions: new[] { "keep" },
                    directDecisionFingerprint: $"summary:{Normalise(record.Summary)}");

            if (!string.IsNullOrWhiteSpace(record.Summary) && record.Summary.Trim().Length is > 0 and < 35)
                Add(findings, record, "thin-summary", "Summary", ResearchAuditSeverity.Info,
                    "Very short summary",
                    "The summary is too short to preserve useful broadcast context.",
                    "Add the main discussion, notable people and any important event or segment.");

            AuditPeople(record, findings);
            AuditTopics(record, findings);
            AuditSources(record, findings);

            if (record.Confidence is > 0 and < 45)
                Add(findings, record, "low-confidence", "Confidence", ResearchAuditSeverity.Info,
                    "Low-confidence research",
                    $"The record is rated at only {record.Confidence}% confidence.",
                    "Find a second source or review the uncertain fields manually.");

            var replayLanguage = ContainsAny(record.Headline + " " + record.Summary, "encore", "replay", "best of", "best-of");
            if (replayLanguage && !record.ResearchState.Equals("encore_or_replay", StringComparison.OrdinalIgnoreCase))
                Add(findings, record, "possible-replay", "Classification", ResearchAuditSeverity.Warning,
                    "Possible replay or encore",
                    "The text suggests reused programming, but the record is not classified as an encore or replay.",
                    "Verify the broadcast type before treating it as an original programme.");
        }


        // Reuse is a corpus-level signal, not thousands of independent user decisions.
        // Keep one informational diagnostic per repeated text pattern and never add it
        // to the rapid decision badge by itself.
        foreach (var group in repeatedSummaryGroups)
        {
            var representative = group.First();
            var boilerplate = IsGenericSummary(representative.Summary);
            Add(findings, representative, "duplicate-summary-pattern", "Summary",
                boilerplate ? ResearchAuditSeverity.Warning : ResearchAuditSeverity.Info,
                boilerplate ? "Repeated boilerplate summary" : "Summary reused across broadcasts",
                $"The wording “{Preview(representative.Summary)}” appears on {group.Count():N0} research records. Reuse can be legitimate for recurring clips, multi-day stories or shared source descriptions.",
                boilerplate
                    ? "Treat this as one corpus-cleanup lead in Advanced diagnostics, not as a separate decision for every date."
                    : "No action is required unless the wording is inaccurate for the affected dates.");
        }

        return new ResearchAuditResult
        {
            CompletedAt = DateTime.Now,
            Findings = findings
                .OrderByDescending(x => x.Severity)
                .ThenBy(x => x.Show, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.BroadcastDate ?? DateTime.MaxValue)
                .ThenBy(x => x.Category, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static void AuditPeople(ResearchAuditRecord record, List<ResearchAuditFinding> findings)
    {
        var showTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            record.Show.Trim(), record.Show.Replace("&", "and", StringComparison.Ordinal).Trim()
        };
        foreach (var guest in record.People.Where(x => x.Role.Equals("guest", StringComparison.OrdinalIgnoreCase))
                     .Select(x => x.Name)
                     .Where(x => showTokens.Contains(x.Trim()) || IsGenericShowGuest(x)))
        {
            Add(findings, record, "show-as-guest", "People", ResearchAuditSeverity.Error,
                "Show name stored as a guest",
                $"“{guest}” is not a specific person and should not be assigned a guest role.",
                "Remove the generic guest entry and retain named people with accurate roles.",
                ResearchAutoFixKind.RemoveGenericGuest, guest);
        }

        if (record.HasAudio && !record.People.Any(x => x.Role.Equals("host", StringComparison.OrdinalIgnoreCase)))
            Add(findings, record, "missing-host", "People", ResearchAuditSeverity.Info,
                "No host recorded",
                "This in-library broadcast has research but no host role is assigned.",
                "Confirm the regular or substitute host lineup for this date.");

        foreach (var group in record.People
                     .Where(x => !string.IsNullOrWhiteSpace(x.Name))
                     .GroupBy(x => NormaliseName(x.Name), StringComparer.OrdinalIgnoreCase))
        {
            var distinctRoles = group.Select(x => Normalise(x.Role)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (distinctRoles.Length > 1)
                Add(findings, record, "contradictory-role", "People", ResearchAuditSeverity.Warning,
                    "Person appears in multiple roles",
                    $"“{group.First().Name}” is assigned as {string.Join(" and ", distinctRoles.Select(RoleDisplay))} on the same broadcast.",
                    "Choose the role that best describes the appearance, or keep all roles when the distinction is intentional.",
                    directDecisionKind: "person-role",
                    directDecisionSubject: group.First().Name.Trim(),
                    directDecisionOptions: distinctRoles,
                    directDecisionFingerprint: $"person:{NormaliseName(group.First().Name)}|roles:{string.Join(",", distinctRoles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}");

            var spellings = group.Select(x => x.Name.Trim()).Distinct(StringComparer.Ordinal).ToArray();
            if (spellings.Length > 1)
                Add(findings, record, "person-name-variant", "People", ResearchAuditSeverity.Info,
                    "Person name has spelling variants",
                    $"The same normalised identity appears as {string.Join(", ", spellings.Select(x => $"“{x}”"))}.",
                    "Choose one canonical display name for this person.",
                    ResearchAutoFixKind.NormalisePersonName, spellings[0]);
        }
    }

    private static void AuditTopics(ResearchAuditRecord record, List<ResearchAuditFinding> findings)
    {
        foreach (var group in record.Topics.Where(x => !string.IsNullOrWhiteSpace(x))
                     .GroupBy(Normalise, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() > 1)
                Add(findings, record, "duplicate-topic", "Topics", ResearchAuditSeverity.Info,
                    "Duplicate topic",
                    $"“{group.First()}” is stored more than once on this broadcast.",
                    "Keep a single canonical topic entry.",
                    ResearchAutoFixKind.RemoveDuplicateTopic, group.Key);

            if (WeakTopics.Contains(group.Key))
                Add(findings, record, "weak-topic", "Topics", ResearchAuditSeverity.Info,
                    "Weak topic label",
                    $"“{group.First()}” is too broad to help discovery or research.",
                    "Keep it when it is useful for this broadcast, or remove it and add specific subjects later.",
                    directDecisionKind: "weak-topic",
                    directDecisionSubject: group.First().Trim(),
                    directDecisionOptions: new[] { "keep", "remove" },
                    directDecisionFingerprint: $"topic:{Normalise(group.First())}");
        }
    }

    private static void AuditSources(ResearchAuditRecord record, List<ResearchAuditFinding> findings)
    {
        if (record.Sources.Count == 0 && (record.Confidence >= 70 || !string.IsNullOrWhiteSpace(record.Summary)))
            Add(findings, record, "unsourced-research", "Sources", ResearchAuditSeverity.Warning,
                "Research has no source",
                "The record contains substantial or high-confidence research without a preserved source.",
                "Add a supporting listing, discussion thread, archive index or listening note.");

        if (record.Confidence >= 75 && record.Sources.Count > 0 && record.Sources.All(x => x.SourceType.Equals("inference", StringComparison.OrdinalIgnoreCase)))
            Add(findings, record, "inference-only-confidence", "Sources", ResearchAuditSeverity.Warning,
                "High confidence based only on inference",
                "The record is highly rated, but every preserved source is classified as inference.",
                "Add a direct source or lower confidence until the details are verified.");

        var duplicateUrls = record.Sources.Where(x => !string.IsNullOrWhiteSpace(x.Url))
            .GroupBy(x => x.Url.Trim(), StringComparer.Ordinal)
            .Where(x => x.Count() > 1)
            .ToList();
        foreach (var group in duplicateUrls)
            Add(findings, record, "duplicate-source", "Sources", ResearchAuditSeverity.Info,
                "Duplicate source link",
                $"The same source URL is preserved {group.Count():N0} times.",
                "Merge the duplicate source entries while preserving the strongest notes and confidence.",
                ResearchAutoFixKind.RemoveDuplicateSource, group.Key);
    }

    private static bool IsWeakHeadline(string value, string show)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var text = Normalise(value);
        return text is "episode" or "archive episode" or "regular episode" or "archive broadcast"
            || text == Normalise(show)
            || text.Contains("faction talk archive broadcast", StringComparison.Ordinal);
    }

    private static bool IsGenericSummary(string value)
        => !string.IsNullOrWhiteSpace(value) && GenericSummaryFragments.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));

    private static bool IsGenericShowGuest(string value)
    {
        var candidate = NormaliseName(value);
        return candidate is "bennington"
            or "ronandfez"
            or "opieandanthony"
            or "theronandronshow"
            or "ronandronshow"
            or "unmasked"
            or "ronbenningtoninterviews";
    }

    private static bool ContainsAny(string value, params string[] terms)
        => terms.Any(x => value.Contains(x, StringComparison.OrdinalIgnoreCase));

    private static string Normalise(string value)
        => string.Join(' ', (value ?? string.Empty).Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string NormaliseName(string value)
        => new string(Normalise(value).Replace("&", "and", StringComparison.Ordinal).Where(char.IsLetterOrDigit).ToArray());

    private static string Preview(string value)
    {
        var normalised = string.Join(' ', (value ?? string.Empty).Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalised.Length <= 120 ? normalised : normalised[..117] + "…";
    }

    private static string RoleDisplay(string role)
        => role.ToLowerInvariant() switch
        {
            "host" => "host",
            "guest" => "guest",
            "caller" => "caller",
            "mentioned" => "mentioned person",
            _ => role
        };

    private static void Add(List<ResearchAuditFinding> target, ResearchAuditRecord record, string ruleId,
        string category, ResearchAuditSeverity severity, string title, string explanation, string action,
        ResearchAutoFixKind autoFixKind = ResearchAutoFixKind.None, string autoFixValue = "",
        string directDecisionKind = "", string directDecisionSubject = "",
        IReadOnlyList<string>? directDecisionOptions = null, string directDecisionFingerprint = "")
    {
        target.Add(new ResearchAuditFinding
        {
            ResearchBroadcastId = record.ResearchBroadcastId,
            EpisodeId = record.EpisodeId,
            Show = record.Show,
            BroadcastDate = record.BroadcastDate,
            RuleId = ruleId,
            Category = category,
            Severity = severity,
            Title = title,
            Explanation = explanation,
            SuggestedAction = action,
            AutoFixKind = autoFixKind,
            AutoFixValue = autoFixValue,
            DirectDecisionKind = directDecisionKind,
            DirectDecisionSubject = directDecisionSubject,
            DirectDecisionOptions = directDecisionOptions ?? Array.Empty<string>(),
            DirectDecisionFingerprint = directDecisionFingerprint
        });
    }
}
