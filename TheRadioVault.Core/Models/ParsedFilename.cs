namespace TheRadioVault.Core.Models;

public sealed class ParsedFilename
{
    public string? CollectionName { get; set; }
    public bool CollectionDetectedFromFilename { get; set; }
    public DateTime? AirDate { get; set; }

    /// <summary>
    /// Optional descriptive phrase recovered from the filename after the show,
    /// date, segment markers and technical noise have been removed.
    /// </summary>
    public string? HeadlineCandidate { get; set; }
    public string HeadlineConfidence { get; set; } = "None";
    public string HeadlineReasoning { get; set; } = "No descriptive phrase was found in the filename.";
    public string? BroadcastType { get; set; }
    public string? StationCandidate { get; set; }
    public string StationConfidence { get; set; } = "None";
    public string StationReasoning { get; set; } = "No station or channel marker was found.";
    public string? MultipartKind { get; set; }
    public string MultipartReasoning { get; set; } = "No multipart marker was found.";
    public string? Edition { get; set; }
    public string EditionReasoning { get; set; } = "No edition marker was found.";
    public string? BroadcastSlot { get; set; }
    public int? TotalParts { get; set; }
    public string? MatchedGrammar { get; set; }
    public int? IgnoredLeadingSequence { get; set; }
    public string DateReasoning { get; set; } = "No supported date pattern was found.";
    public string ParserVersion { get; set; } = "0.13.0";
    public int MetadataConfidence { get; set; }
    public string MetadataConfidenceReasoning { get; set; } = "Metadata confidence has not been calculated.";

    // Kept as a compatibility alias while the database column is still named Title.
    public string? Title
    {
        get => HeadlineCandidate;
        set => HeadlineCandidate = value;
    }

    public string DateConfidence { get; set; } = "Unknown";
    public int PartNumber { get; set; } = 1;
}
