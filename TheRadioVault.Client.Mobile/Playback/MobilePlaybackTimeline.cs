using TheRadioVault.Web.Models;

namespace TheRadioVault.Client.Mobile.Playback;

/// <summary>
/// Maps a logical broadcast timeline onto one or more decoder media parts.
/// It owns position clamping, part selection, decoder-settling protection and
/// completion state, but performs no media, network or UI side effects.
/// </summary>
internal sealed class MobilePlaybackTimeline
{
    private const long DecoderAlignmentToleranceMs = 1_500;
    private const long CompletionToleranceMs = 5_000;
    private long? _pendingDecoderLogicalPositionMs;
    private DateTimeOffset _pendingDecoderPositionUntil;

    public IReadOnlyList<WebCanonicalMediaPart> Parts { get; private set; } = [];
    public int PartIndex { get; private set; }
    public long PositionMs { get; private set; }
    public long DurationMs { get; private set; }
    public bool Completed { get; private set; }
    public bool HasParts => Parts.Count > 0;
    public double Progress => DurationMs <= 0
        ? 0d
        : Math.Clamp(PositionMs / (double)DurationMs, 0d, 1d);

    public void Load(IEnumerable<WebCanonicalMediaPart> parts, long declaredDurationMs)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var orderedParts = parts.OrderBy(part => part.PartNumber).ToArray();
        if (orderedParts.Length == 0)
            throw new InvalidOperationException("This broadcast has no playable media parts.");
        Parts = orderedParts;
        DurationMs = Math.Max(
            Math.Max(0, declaredDurationMs),
            Parts.Max(part => Math.Max(0, part.LogicalEndMs)));
        PartIndex = 0;
        PositionMs = 0;
        Completed = false;
        _pendingDecoderLogicalPositionMs = null;
        _pendingDecoderPositionUntil = default;
    }

    public void Reset()
    {
        Parts = [];
        PartIndex = 0;
        PositionMs = 0;
        DurationMs = 0;
        Completed = false;
        _pendingDecoderLogicalPositionMs = null;
        _pendingDecoderPositionUntil = default;
    }

    public long ClampPosition(long logicalPositionMs)
        => Math.Clamp(logicalPositionMs, 0, Math.Max(0, DurationMs));

    public int FindPartIndex(long logicalPositionMs)
    {
        if (Parts.Count == 0) return -1;
        var clamped = ClampPosition(logicalPositionMs);
        for (var index = 0; index < Parts.Count; index++)
        {
            var part = Parts[index];
            if (clamped >= part.LogicalStartMs && clamped < part.LogicalEndMs)
                return index;
        }
        return Parts.Count - 1;
    }

    public WebCanonicalMediaPart SelectPart(long logicalPositionMs)
    {
        if (Parts.Count == 0)
            throw new InvalidOperationException("This broadcast has no playable media parts.");
        SetPosition(logicalPositionMs);
        PartIndex = FindPartIndex(PositionMs);
        return Parts[PartIndex];
    }

    public TimeSpan LocalPosition(long logicalPositionMs)
    {
        if (Parts.Count == 0 || PartIndex < 0 || PartIndex >= Parts.Count)
            return TimeSpan.Zero;
        return TimeSpan.FromMilliseconds(Math.Max(
            0,
            ClampPosition(logicalPositionMs) - Parts[PartIndex].LogicalStartMs));
    }

    public void SetPosition(long logicalPositionMs)
    {
        PositionMs = ClampPosition(logicalPositionMs);
        if (PositionMs < Math.Max(0, DurationMs - CompletionToleranceMs))
            Completed = false;
    }

    public void PrepareDecoder(long logicalPositionMs, DateTimeOffset now, TimeSpan settleWindow)
    {
        SetPosition(logicalPositionMs);
        _pendingDecoderLogicalPositionMs = PositionMs;
        _pendingDecoderPositionUntil = now.Add(settleWindow);
    }

    public long CaptureDecoderPosition(TimeSpan decoderPosition, DateTimeOffset now)
    {
        if (PartIndex < 0 || PartIndex >= Parts.Count) return Math.Max(0, PositionMs);
        var observed = Parts[PartIndex].LogicalStartMs + (long)decoderPosition.TotalMilliseconds;
        if (_pendingDecoderLogicalPositionMs is { } pending)
        {
            if (Math.Abs(observed - pending) <= DecoderAlignmentToleranceMs)
            {
                _pendingDecoderLogicalPositionMs = null;
            }
            else if (now <= _pendingDecoderPositionUntil)
            {
                PositionMs = ClampPosition(pending);
                return PositionMs;
            }
            else
            {
                _pendingDecoderLogicalPositionMs = null;
            }
        }
        PositionMs = ClampPosition(observed);
        return PositionMs;
    }

    public bool TryGetNextPart(out WebCanonicalMediaPart? part)
    {
        if (PartIndex + 1 < Parts.Count)
        {
            part = Parts[PartIndex + 1];
            return true;
        }
        part = null;
        return false;
    }

    public void MarkCompleted(DateTimeOffset now, TimeSpan settleWindow)
    {
        Completed = true;
        PositionMs = DurationMs;
        _pendingDecoderLogicalPositionMs = DurationMs;
        _pendingDecoderPositionUntil = now.Add(settleWindow);
    }

    public bool IsCompleted()
        => Completed || DurationMs > 0 && PositionMs >= Math.Max(0, DurationMs - CompletionToleranceMs);
}
