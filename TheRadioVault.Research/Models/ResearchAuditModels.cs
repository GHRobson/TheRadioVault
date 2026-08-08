namespace TheRadioVault.Research.Models;

public enum ResearchAuditSeverity
{
    Info,
    Warning,
    Error
}

public enum ResearchAutoFixKind
{
    None,
    RemoveGenericGuest,
    RemoveDuplicateTopic,
    NormalisePersonName,
    RemoveDuplicateSource,
    ClearGenericHeadline
}

public sealed record ResearchAuditPerson(string Name, string Role);

public sealed record ResearchAuditSource(
    string Url,
    string Title,
    string SourceType,
    int Confidence);

public sealed class ResearchAuditRecord
{
    public long ResearchBroadcastId { get; init; }
    public long? EpisodeId { get; init; }
    public string Show { get; init; } = string.Empty;
    public DateTime? BroadcastDate { get; init; }
    public string Headline { get; init; } = string.Empty;
    public string Summary { get; init; } = string.Empty;
    public string ResearchState { get; init; } = string.Empty;
    public int Confidence { get; init; }
    public bool HasAudio { get; init; }
    public IReadOnlyList<ResearchAuditPerson> People { get; init; } = Array.Empty<ResearchAuditPerson>();
    public IReadOnlyList<string> Topics { get; init; } = Array.Empty<string>();
    public IReadOnlyList<ResearchAuditSource> Sources { get; init; } = Array.Empty<ResearchAuditSource>();
}

public sealed class ResearchAuditFinding
{
    public long ResearchBroadcastId { get; init; }
    public long? EpisodeId { get; init; }
    public string Show { get; init; } = string.Empty;
    public DateTime? BroadcastDate { get; init; }
    public string RuleId { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public ResearchAuditSeverity Severity { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Explanation { get; init; } = string.Empty;
    public string SuggestedAction { get; init; } = string.Empty;
    public ResearchAutoFixKind AutoFixKind { get; init; }
    public string AutoFixValue { get; init; } = string.Empty;
    public string DirectDecisionKind { get; init; } = string.Empty;
    public string DirectDecisionSubject { get; init; } = string.Empty;
    public IReadOnlyList<string> DirectDecisionOptions { get; init; } = Array.Empty<string>();
    public string DirectDecisionFingerprint { get; init; } = string.Empty;
}

public sealed class ResearchAuditResult
{
    public DateTime CompletedAt { get; init; } = DateTime.Now;
    public IReadOnlyList<ResearchAuditFinding> Findings { get; init; } = Array.Empty<ResearchAuditFinding>();
    public int ErrorCount => Findings.Count(x => x.Severity == ResearchAuditSeverity.Error);
    public int WarningCount => Findings.Count(x => x.Severity == ResearchAuditSeverity.Warning);
    public int InfoCount => Findings.Count(x => x.Severity == ResearchAuditSeverity.Info);
    public int AffectedBroadcasts => Findings.Select(x => x.ResearchBroadcastId).Distinct().Count();
    public int SafeFixCount => Findings.Count(x => x.AutoFixKind != ResearchAutoFixKind.None);
}
