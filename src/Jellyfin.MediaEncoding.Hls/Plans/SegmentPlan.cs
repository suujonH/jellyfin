using System;
using System.Collections.Generic;

namespace Jellyfin.MediaEncoding.Hls.Plans;

/// <summary>
/// The segments of an HLS stream: the one source of the playlist, the ffmpeg cuts, where a run starts and how its output is checked.
/// </summary>
/// <remarks>
/// Times are on the plan timeline: the source pts minus the format start time, the timeline of ffmpeg <c>-copyts -start_at_zero</c>.
/// A plan is a pure function of its inputs, so a lost session is rebuilt with the same segments.
/// </remarks>
public sealed class SegmentPlan
{
    // Container timestamps are rounded to milliseconds and the segment list prints microseconds.
    private static readonly long _defaultToleranceTicks = TimeSpan.FromMilliseconds(10).Ticks;

    private readonly long[] _starts;

    internal SegmentPlan(SegmentPlanKind kind, IReadOnlyList<long> starts, long totalTicks, long frameTicks, IReadOnlyList<long> keyframes)
    {
        _starts = [.. starts];
        var segments = new HlsSegment[_starts.Length];
        long longest = 0;
        for (var i = 0; i < _starts.Length; i++)
        {
            var end = i + 1 < _starts.Length ? _starts[i + 1] : totalTicks;
            segments[i] = new HlsSegment(i, _starts[i], end - _starts[i]);
            longest = Math.Max(longest, end - _starts[i]);
        }

        Kind = kind;
        Segments = segments;
        TotalTicks = totalTicks;
        TargetDurationSeconds = (int)Math.Max(1, (longest + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond);
        FrameTicks = frameTicks;
        ToleranceTicks = GetToleranceTicks(frameTicks);
        Keyframes = keyframes;
    }

    /// <summary>
    /// Gets how the boundaries were chosen.
    /// </summary>
    public SegmentPlanKind Kind { get; }

    /// <summary>
    /// Gets the segments, in order and without gaps, from 0 to <see cref="TotalTicks"/>.
    /// </summary>
    public IReadOnlyList<HlsSegment> Segments { get; }

    /// <summary>
    /// Gets the end of the last segment.
    /// </summary>
    public long TotalTicks { get; }

    /// <summary>
    /// Gets the longest segment in whole seconds, rounded up, for <c>#EXT-X-TARGETDURATION</c>.
    /// </summary>
    public int TargetDurationSeconds { get; }

    /// <summary>
    /// Gets the duration of a frame of the video the plan was made for, or 0 when it is not known (audio alone).
    /// </summary>
    public long FrameTicks { get; }

    /// <summary>
    /// Gets how far an ffmpeg cut may lie from a boundary and still be that boundary: half a frame, or 10 ms without a frame rate.
    /// </summary>
    public long ToleranceTicks { get; }

    /// <summary>
    /// Gets the keyframes of a keyframe plan between its start and its end, in order; the boundaries are some of them.
    /// Empty for a uniform plan.
    /// </summary>
    public IReadOnlyList<long> Keyframes { get; }

    /// <summary>
    /// Gets the index of the segment that contains a time; times outside the plan give the first or the last segment.
    /// </summary>
    /// <param name="ticks">The time on the plan timeline.</param>
    /// <returns>The segment index.</returns>
    public int IndexAt(long ticks)
    {
        var index = Array.BinarySearch(_starts, ticks);

        // Without an exact match the complement is the index of the next start.
        return Math.Clamp(index >= 0 ? index : ~index - 1, 0, _starts.Length - 1);
    }

    /// <summary>
    /// Gets the last keyframe that lies before a time by more than the tolerance; 0, the start, when there is none.
    /// </summary>
    /// <param name="ticks">The time on the plan timeline.</param>
    /// <returns>The keyframe time.</returns>
    internal long GetKeyframeBefore(long ticks)
    {
        long result = 0;
        foreach (var keyframe in Keyframes)
        {
            if (keyframe >= ticks - ToleranceTicks)
            {
                break;
            }

            result = keyframe;
        }

        return result;
    }

    internal static long GetToleranceTicks(long frameTicks) => frameTicks > 0 ? frameTicks / 2 : _defaultToleranceTicks;
}
