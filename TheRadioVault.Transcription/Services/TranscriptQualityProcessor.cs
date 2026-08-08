using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Services;

public static class TranscriptQualityProcessor
{
    private static readonly Regex MusicMarker = new(@"(?:♪|♫|\[(?:music|applause)\]|\((?:upbeat |theme |intro |outro )?music\))", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SilenceMarker = new(@"\[(?:blank_audio|silence)\]|\((?:silence|dead air)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NonSpeechMarker = new(@"\[(?:laughter|applause|noise)\]|\((?:laughter|applause|crowd noise)\)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IReadOnlyList<TranscriptSegment> Process(IReadOnlyList<TranscriptSegment>? source)
    {
        var classified = (source ?? Array.Empty<TranscriptSegment>())
            .OrderBy(x => x.Index)
            .Select(Classify)
            .ToList();
        var merged = new List<TranscriptSegment>();
        foreach (var segment in classified)
        {
            if (segment.ContentKind is TranscriptContentKind.Music or TranscriptContentKind.Silence or TranscriptContentKind.NonSpeech
                && merged.LastOrDefault() is { } previous
                && previous.ContentKind == segment.ContentKind
                && segment.StartMs - previous.EndMs <= 5000)
            {
                merged[^1] = previous with
                {
                    EndMs = Math.Max(previous.EndMs, segment.EndMs),
                    Text = MarkerText(segment.ContentKind),
                    Confidence = Average(previous.Confidence, segment.Confidence),
                    Words = Array.Empty<TranscriptWord>(),
                    IsReviewed = previous.IsReviewed || segment.IsReviewed
                };
                continue;
            }
            merged.Add(segment);
        }
        return merged.Select((x, index) => x with { Index = index }).ToList();
    }

    public static TranscriptDocument Process(TranscriptDocument document)
    {
        var segments = Process(document.Segments);
        var fullText = string.Join(Environment.NewLine, segments.Select(x => x.Text));
        return new TranscriptDocument
        {
            Id = document.Id,
            EpisodeId = document.EpisodeId,
            Status = document.Status,
            Language = document.Language,
            EngineId = document.EngineId,
            EngineVersion = document.EngineVersion,
            ModelId = document.ModelId,
            Source = document.Source,
            FullText = fullText,
            WordCount = fullText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            DurationMs = document.DurationMs,
            HasWordTimings = segments.Any(x => x.Words?.Count > 0),
            HasSpeakerDiarization = document.HasSpeakerDiarization,
            CreatedAt = document.CreatedAt,
            UpdatedAt = document.UpdatedAt,
            CompletedAt = document.CompletedAt,
            Revision = document.Revision,
            MetadataJson = document.MetadataJson,
            Segments = segments,
            Speakers = document.Speakers
        };
    }

    public static TranscriptContentKind DetectKind(string? text)
    {
        var value = text ?? "";
        if (SilenceMarker.IsMatch(value)) return TranscriptContentKind.Silence;
        if (MusicMarker.IsMatch(value)) return TranscriptContentKind.Music;
        if (NonSpeechMarker.IsMatch(value)) return TranscriptContentKind.NonSpeech;
        return string.IsNullOrWhiteSpace(value) ? TranscriptContentKind.Unknown : TranscriptContentKind.Speech;
    }

    private static TranscriptSegment Classify(TranscriptSegment segment)
    {
        var kind = segment.ContentKind == TranscriptContentKind.Speech
            ? DetectKind(segment.Text)
            : segment.ContentKind;
        if (kind == TranscriptContentKind.Speech && IsPathologicalSpeech(segment))
            kind = TranscriptContentKind.Unknown;
        if (kind == TranscriptContentKind.Speech) return segment;
        return segment with
        {
            ContentKind = kind,
            Text = MarkerText(kind),
            Words = Array.Empty<TranscriptWord>()
        };
    }

    private static bool IsPathologicalSpeech(TranscriptSegment segment)
    {
        var tokens = Regex.Matches(segment.Text ?? string.Empty, @"[\p{L}\p{N}']+")
            .Select(match => match.Value.ToLowerInvariant())
            .ToArray();
        if (tokens.Length == 0) return false;
        var durationMs = Math.Max(0, segment.EndMs - segment.StartMs);
        if (durationMs == 0) return true;

        var plausibleWords = 10 + (int)Math.Ceiling(durationMs / 1000d * 6d);
        if (tokens.Length > plausibleWords) return true;
        if (tokens.Length < 12) return false;
        var distinct = tokens.Distinct(StringComparer.Ordinal).Count();
        return distinct <= Math.Max(2, tokens.Length / 6);
    }

    private static string MarkerText(TranscriptContentKind kind) => kind switch
    {
        TranscriptContentKind.Music => "[Music]",
        TranscriptContentKind.Silence => "[Silence]",
        TranscriptContentKind.NonSpeech => "[Non-speech]",
        _ => "[Unclear audio]"
    };

    private static double? Average(double? a, double? b)
    {
        if (!a.HasValue) return b;
        if (!b.HasValue) return a;
        return (a.Value + b.Value) / 2d;
    }
}

public static class TranscriptMetadataSanitizer
{
    private static readonly HashSet<string> PortableKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "worker", "workerVersion", "modelFile", "language", "rangeStartMs", "rangeDurationMs",
        "tinyDiarize", "tinyDiarizeLimit", "voiceActivityDetection", "backend", "processingDurationMs",
        "audioDurationMs", "speedMultiplier", "contextTerms", "qualityProcessor"
    };

    public static string CreatePortableMetadata(string? metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson)) return "{}";
        try
        {
            using var document = JsonDocument.Parse(metadataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return "{}";
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!PortableKeys.Contains(property.Name)) continue;
                values[property.Name] = ConvertValue(property.Value);
            }
            return JsonSerializer.Serialize(values, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }
        catch
        {
            return "{}";
        }
    }

    private static object? ConvertValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number when element.TryGetDouble(out var number) => number,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => element.EnumerateArray().Select(ConvertValue).ToArray(),
        _ => element.GetRawText()
    };
}
