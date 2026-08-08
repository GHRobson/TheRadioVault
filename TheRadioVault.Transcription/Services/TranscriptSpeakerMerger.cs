using TheRadioVault.Transcription.Models;

namespace TheRadioVault.Transcription.Services;

public static class TranscriptSpeakerMerger
{
    public static IReadOnlyList<TranscriptSegment> Apply(
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<SpeakerDiarizationTurn> turns)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(turns);
        if (segments.Count == 0 || turns.Count == 0) return segments;

        var orderedTurns = turns
            .Where(x => x.EndMs > x.StartMs && !string.IsNullOrWhiteSpace(x.SpeakerKey))
            .OrderBy(x => x.StartMs)
            .ThenBy(x => x.EndMs)
            .ToArray();
        if (orderedTurns.Length == 0) return segments;

        var result = new List<TranscriptSegment>();
        foreach (var segment in segments.OrderBy(x => x.Index))
        {
            var words = segment.Words?.Where(x => x.EndMs >= x.StartMs).ToArray() ?? Array.Empty<TranscriptWord>();
            if (words.Length == 0)
            {
                var turn = FindBestTurn(segment.StartMs, segment.EndMs, orderedTurns);
                result.Add(CopySegment(segment, result.Count, turn, segment.Words));
                continue;
            }

            var labelledWords = words
                .Select(word =>
                {
                    var turn = FindBestTurn(word.StartMs, word.EndMs, orderedTurns);
                    return new LabelledWord(word with { SpeakerKey = turn?.SpeakerKey ?? string.Empty }, turn);
                })
                .ToArray();

            var groupStart = 0;
            while (groupStart < labelledWords.Length)
            {
                var groupTurn = labelledWords[groupStart].Turn;
                var groupEnd = groupStart + 1;
                while (groupEnd < labelledWords.Length
                       && string.Equals(labelledWords[groupEnd].Turn?.SpeakerKey, groupTurn?.SpeakerKey, StringComparison.Ordinal))
                {
                    groupEnd++;
                }

                var groupWords = labelledWords[groupStart..groupEnd].Select(x => x.Word).ToArray();
                var text = JoinWords(groupWords);
                result.Add(new TranscriptSegment(
                    result.Count,
                    groupWords[0].StartMs,
                    Math.Max(groupWords[0].StartMs, groupWords[^1].EndMs),
                    text.Length == 0 ? segment.Text : text,
                    groupTurn?.SpeakerLabel ?? string.Empty,
                    segment.Confidence,
                    groupWords,
                    groupTurn?.SpeakerKey ?? string.Empty,
                    ContentKind: segment.ContentKind,
                    IsReviewed: segment.IsReviewed));
                groupStart = groupEnd;
            }
        }

        return result;
    }

    private static TranscriptSegment CopySegment(
        TranscriptSegment source,
        int index,
        SpeakerDiarizationTurn? turn,
        IReadOnlyList<TranscriptWord>? words)
        => new(
            index,
            source.StartMs,
            source.EndMs,
            source.Text,
            turn?.SpeakerLabel ?? string.Empty,
            source.Confidence,
            words,
            turn?.SpeakerKey ?? string.Empty,
            source.AssignedPersonName,
            source.SpeakerConfidence,
            source.AssignmentState,
            source.ContentKind,
            source.IsReviewed);

    private static SpeakerDiarizationTurn? FindBestTurn(
        long startMs,
        long endMs,
        IReadOnlyList<SpeakerDiarizationTurn> turns)
    {
        endMs = Math.Max(startMs + 1, endMs);
        SpeakerDiarizationTurn? best = null;
        long bestOverlap = 0;
        foreach (var turn in turns)
        {
            if (turn.StartMs >= endMs) break;
            if (turn.EndMs <= startMs) continue;
            var overlap = Math.Min(endMs, turn.EndMs) - Math.Max(startMs, turn.StartMs);
            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = turn;
            }
        }

        if (best is not null) return best;
        var midpoint = startMs + ((endMs - startMs) / 2);
        return turns.OrderBy(x => DistanceFrom(midpoint, x)).FirstOrDefault();
    }

    private static long DistanceFrom(long point, SpeakerDiarizationTurn turn)
        => point < turn.StartMs ? turn.StartMs - point : point > turn.EndMs ? point - turn.EndMs : 0;

    private static string JoinWords(IReadOnlyList<TranscriptWord> words)
        => string.Join(" ", words.Select(x => x.Text.Trim()).Where(x => x.Length > 0))
            .Replace(" ,", ",", StringComparison.Ordinal)
            .Replace(" .", ".", StringComparison.Ordinal)
            .Replace(" ?", "?", StringComparison.Ordinal)
            .Replace(" !", "!", StringComparison.Ordinal)
            .Replace(" ;", ";", StringComparison.Ordinal)
            .Replace(" :", ":", StringComparison.Ordinal)
            .Trim();

    private sealed record LabelledWord(TranscriptWord Word, SpeakerDiarizationTurn? Turn);
}
