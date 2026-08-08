using System.Text;
using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Services;

public sealed class TranscriptReviewService
{
    public TranscriptDocument UpdatePhrase(TranscriptDocument document, int segmentIndex, string text)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("A transcript phrase cannot be empty.");

        var segments = document.Segments.ToList();
        var position = FindPosition(segments, segmentIndex);
        segments[position] = segments[position] with { Text = text.Trim(), IsReviewed = true };
        return Rebuild(document, segments);
    }

    public TranscriptDocument SetReviewed(TranscriptDocument document, int segmentIndex, bool reviewed)
    {
        ArgumentNullException.ThrowIfNull(document);
        var segments = document.Segments.ToList();
        var position = FindPosition(segments, segmentIndex);
        segments[position] = segments[position] with { IsReviewed = reviewed };
        return Rebuild(document, segments);
    }

    public TranscriptDocument SplitPhrase(TranscriptDocument document, int segmentIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        var segments = document.Segments.ToList();
        var position = FindPosition(segments, segmentIndex);
        var source = segments[position];
        var parts = SplitText(source.Text);
        if (parts is null)
            throw new InvalidOperationException("This phrase is too short to split.");

        var splitMs = FindSplitTime(source, parts.Value.First);
        var firstWords = source.Words?.Where(x => x.EndMs <= splitMs).ToArray() ?? Array.Empty<TranscriptWord>();
        var secondWords = source.Words?.Where(x => x.EndMs > splitMs).ToArray() ?? Array.Empty<TranscriptWord>();
        var first = source with
        {
            Text = parts.Value.First,
            EndMs = splitMs,
            Words = firstWords,
            IsReviewed = true
        };
        var second = source with
        {
            Index = source.Index + 1,
            Text = parts.Value.Second,
            StartMs = splitMs,
            Words = secondWords,
            IsReviewed = true
        };
        segments[position] = first;
        segments.Insert(position + 1, second);
        return Rebuild(document, segments);
    }

    public TranscriptDocument MergeWithNext(TranscriptDocument document, int segmentIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        var segments = document.Segments.ToList();
        var position = FindPosition(segments, segmentIndex);
        if (position >= segments.Count - 1)
            throw new InvalidOperationException("There is no following phrase to merge.");

        var first = segments[position];
        var second = segments[position + 1];
        if (!SameSpeaker(first, second))
            throw new InvalidOperationException("Only consecutive phrases from the same speaker can be merged.");

        segments[position] = first with
        {
            EndMs = Math.Max(first.EndMs, second.EndMs),
            Text = $"{first.Text.Trim()} {second.Text.Trim()}".Trim(),
            Words = (first.Words ?? Array.Empty<TranscriptWord>())
                .Concat(second.Words ?? Array.Empty<TranscriptWord>())
                .OrderBy(x => x.StartMs)
                .ToArray(),
            IsReviewed = true
        };
        segments.RemoveAt(position + 1);
        return Rebuild(document, segments);
    }

    public string ExportPlainText(TranscriptDocument document, TranscriptSummary? summary = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new StringBuilder();
        if (summary is not null)
        {
            builder.AppendLine(summary.EpisodeTitle);
            builder.AppendLine($"{summary.Show} · {summary.AirDateDisplay}");
            builder.AppendLine();
        }
        foreach (var segment in document.Segments)
        {
            var speaker = SpeakerPrefix(segment);
            builder.Append('[').Append(FormatPlainTime(segment.StartMs)).Append("] ")
                .Append(speaker).AppendLine(segment.Text.Trim());
        }
        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    public string ExportSrt(TranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new StringBuilder();
        for (var index = 0; index < document.Segments.Count; index++)
        {
            var segment = document.Segments[index];
            builder.AppendLine((index + 1).ToString());
            builder.Append(FormatSubtitleTime(segment.StartMs, ','))
                .Append(" --> ").AppendLine(FormatSubtitleTime(segment.EndMs, ','));
            builder.Append(SpeakerPrefix(segment)).AppendLine(segment.Text.Trim());
            builder.AppendLine();
        }
        return builder.ToString();
    }

    public string ExportVtt(TranscriptDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var builder = new StringBuilder("WEBVTT\n\n");
        foreach (var segment in document.Segments)
        {
            builder.Append(FormatSubtitleTime(segment.StartMs, '.'))
                .Append(" --> ").AppendLine(FormatSubtitleTime(segment.EndMs, '.'));
            builder.Append(SpeakerPrefix(segment)).AppendLine(segment.Text.Trim());
            builder.AppendLine();
        }
        return builder.ToString();
    }

    private static int FindPosition(IReadOnlyList<TranscriptSegment> segments, int segmentIndex)
    {
        var position = segments.ToList().FindIndex(x => x.Index == segmentIndex);
        return position >= 0 ? position : throw new InvalidOperationException("The selected phrase no longer exists.");
    }

    private static (string First, string Second)? SplitText(string text)
    {
        var words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return null;
        var midpoint = Math.Clamp(words.Length / 2, 1, words.Length - 1);
        return (string.Join(' ', words[..midpoint]), string.Join(' ', words[midpoint..]));
    }

    private static long FindSplitTime(TranscriptSegment source, string firstText)
    {
        var firstWordCount = firstText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        var timedWords = source.Words?.OrderBy(x => x.StartMs).ToArray() ?? Array.Empty<TranscriptWord>();
        if (timedWords.Length >= 2 && firstWordCount > 0 && firstWordCount < timedWords.Length)
        {
            var left = timedWords[firstWordCount - 1].EndMs;
            var right = timedWords[firstWordCount].StartMs;
            return Math.Clamp(left + Math.Max(0, right - left) / 2, source.StartMs + 1, Math.Max(source.StartMs + 1, source.EndMs - 1));
        }
        return source.StartMs + Math.Max(1, (source.EndMs - source.StartMs) / 2);
    }

    private static bool SameSpeaker(TranscriptSegment first, TranscriptSegment second)
    {
        if (!string.IsNullOrWhiteSpace(first.SpeakerKey) || !string.IsNullOrWhiteSpace(second.SpeakerKey))
            return string.Equals(first.SpeakerKey, second.SpeakerKey, StringComparison.OrdinalIgnoreCase);
        return string.Equals(first.DisplaySpeaker, second.DisplaySpeaker, StringComparison.OrdinalIgnoreCase);
    }

    private static TranscriptDocument Rebuild(TranscriptDocument source, IReadOnlyList<TranscriptSegment> segments)
    {
        var normalized = segments.Select((segment, index) => segment with { Index = index }).ToArray();
        return new TranscriptDocument
        {
            Id = source.Id,
            EpisodeId = source.EpisodeId,
            Status = source.Status,
            Language = source.Language,
            EngineId = source.EngineId,
            EngineVersion = source.EngineVersion,
            ModelId = source.ModelId,
            Source = source.Source,
            FullText = string.Empty,
            WordCount = 0,
            DurationMs = source.DurationMs,
            HasWordTimings = source.HasWordTimings,
            HasSpeakerDiarization = source.HasSpeakerDiarization,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            CompletedAt = source.CompletedAt,
            Revision = source.Revision,
            MetadataJson = source.MetadataJson,
            Segments = normalized,
            Speakers = source.Speakers
        };
    }

    private static string SpeakerPrefix(TranscriptSegment segment)
        => string.IsNullOrWhiteSpace(segment.DisplaySpeaker) ? string.Empty : $"{segment.DisplaySpeaker}: ";

    private static string FormatPlainTime(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return value.TotalHours >= 1 ? value.ToString(@"h\:mm\:ss") : value.ToString(@"m\:ss");
    }

    private static string FormatSubtitleTime(long milliseconds, char separator)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}{separator}{value.Milliseconds:000}";
    }
}
