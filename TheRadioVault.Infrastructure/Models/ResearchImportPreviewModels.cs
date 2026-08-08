namespace TheRadioVault.Models;

public sealed class ResearchImportPreview
{
    public string PackageName { get; set; } = string.Empty;
    public string Show { get; set; } = string.Empty;
    public int TotalRecords { get; set; }
    public int ExactMatches { get; set; }
    public int MissingRecords { get; set; }
    public int AmbiguousMatches { get; set; }
    public int NewPeople { get; set; }
    public int NewTopics { get; set; }
    public int NewSources { get; set; }
    public int IncomingSummaries { get; set; }
    public int ProtectedManualRecords { get; set; }
    public int PotentialConflicts { get; set; }
    public int FieldsExpectedToApply { get; set; }
    public int FieldsExpectedToMerge { get; set; }
    public int FieldsExpectedToPreserve { get; set; }
    public int FieldsProtectedByManualEdits { get; set; }
    public bool AuthoritativeAudit { get; set; }
    public bool PreviouslyImported { get; set; }
    public string PackageHash { get; set; } = string.Empty;
    public string Summary => $"{TotalRecords:N0} records · {ExactMatches:N0} matched · {MissingRecords:N0} without audio · {AmbiguousMatches:N0} need review";
    public string MergeSummary => AuthoritativeAudit
        ? $"{FieldsExpectedToApply:N0} audited fields will replace or clear stale values · {FieldsExpectedToPreserve:N0} already match"
        : $"{FieldsExpectedToApply:N0} fields will improve · {FieldsExpectedToMerge:N0} lists will merge · {FieldsExpectedToPreserve:N0} values will remain";
}


public sealed record ResearchPackOperationProgress(int Current, int Total, string Message)
{
    public double Percent => Total <= 0 ? 0 : Math.Clamp(Current * 100d / Total, 0, 100);
}
