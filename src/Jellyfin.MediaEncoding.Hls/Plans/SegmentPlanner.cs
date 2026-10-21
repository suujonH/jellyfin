using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.MediaEncoding.Keyframes;

namespace Jellyfin.MediaEncoding.Hls.Plans;

/// <summary>
/// Creates <see cref="SegmentPlan"/>s. Equal inputs give equal plans.
/// </summary>
public static class SegmentPlanner
{
    /// <summary>
    /// The shortest average keyframe interval a keyframe plan accepts. Live HLS opens a file at every keyframe, so denser keyframes
    /// (intra-only video) would make a file of every few frames; such a video is transcoded instead.
    /// </summary>
    public static readonly TimeSpan MinimumKeyframeInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The longest segment a keyframe plan accepts, unless three segment lengths are longer. A player has to fetch a whole
    /// segment before it plays it; a video with keyframes further apart is transcoded instead.
    /// </summary>
    public static readonly TimeSpan MaximumKeyframeSegment = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Creates a plan whose boundaries are keyframes, for copying the video.
    /// </summary>
    /// <param name="keyframes">The keyframes, on the plan timeline.</param>
    /// <param name="totalTicks">The duration of the stream.</param>
    /// <param name="segmentLengthTicks">The desired segment length.</param>
    /// <param name="frameRate">The frame rate of the video, if known.</param>
    /// <returns>The plan, or null when the keyframes are missing, too dense or too far apart.</returns>
    public static SegmentPlan? CreateKeyframePlan(KeyframeData keyframes, long totalTicks, long segmentLengthTicks, double? frameRate)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentLengthTicks);

        var source = keyframes.KeyframeTicks;
        if (source.Count == 0)
        {
            return null;
        }

        // Keyframe data from elsewhere, such as a TrueCache entry, can list the same time more than once.
        var distinct = source.Distinct().Count();
        if (distinct > 1 && (source.Max() - source.Min()) / (distinct - 1) < MinimumKeyframeInterval.Ticks)
        {
            return null;
        }

        var frameTicks = GetFrameTicks(frameRate);
        var tolerance = SegmentPlan.GetToleranceTicks(frameTicks);

        // The first segment starts at 0 whatever the first keyframe: a run from the start opens its first file there.
        // A keyframe within the tolerance of either end cannot start a segment.
        var usable = new List<long>(source.Count);
        foreach (var keyframe in source.Order())
        {
            if (keyframe > tolerance && keyframe < totalTicks - tolerance && (usable.Count == 0 || keyframe > usable[^1]))
            {
                usable.Add(keyframe);
            }
        }

        var starts = new List<long> { 0 };
        var next = 0;
        while (true)
        {
            // The keyframe closest to one segment length after the last boundary, but at least half a length after it; the
            // end of the stream counts as a candidate, so a short remainder joins the last segment. A tie goes to the later one.
            var last = starts[^1];
            while (next < usable.Count && usable[next] < last + (segmentLengthTicks / 2))
            {
                next++;
            }

            if (next == usable.Count)
            {
                break;
            }

            var target = last + segmentLengthTicks;
            var above = next;
            while (above < usable.Count && usable[above] < target)
            {
                above++;
            }

            var aboveTicks = above < usable.Count ? usable[above] : totalTicks;
            int pick;
            if (above > next && target - usable[above - 1] < aboveTicks - target)
            {
                pick = above - 1;
            }
            else if (above < usable.Count)
            {
                pick = above;
            }
            else
            {
                break;
            }

            starts.Add(usable[pick]);
            next = pick + 1;
        }

        var plan = new SegmentPlan(SegmentPlanKind.Keyframe, starts, totalTicks, frameTicks, usable);
        var longest = plan.Segments.Max(s => s.DurationTicks);
        return longest > Math.Max(MaximumKeyframeSegment.Ticks, 3 * segmentLengthTicks) ? null : plan;
    }

    /// <summary>
    /// Creates a plan whose boundaries are one segment length apart, for transcoding the video or for audio alone.
    /// </summary>
    /// <param name="totalTicks">The duration of the stream.</param>
    /// <param name="segmentLengthTicks">The desired segment length.</param>
    /// <param name="frameRate">The frame rate of the transcoded video; null for audio alone.</param>
    /// <returns>The plan.</returns>
    public static SegmentPlan CreateUniformPlan(long totalTicks, long segmentLengthTicks, double? frameRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(totalTicks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(segmentLengthTicks);

        var frameTicks = GetFrameTicks(frameRate);
        var length = segmentLengthTicks;
        if (frameTicks > 0)
        {
            // A whole number of frames, L' = ceil(L * fps) / fps, so every boundary falls on a frame of a constant frame rate.
            var fps = frameRate.GetValueOrDefault();
            var frames = Math.Ceiling((segmentLengthTicks * fps / TimeSpan.TicksPerSecond) - 1e-6);
            length = (long)Math.Round(frames * TimeSpan.TicksPerSecond / fps);
        }

        // A remainder within the tolerance joins the last segment.
        var tolerance = SegmentPlan.GetToleranceTicks(frameTicks);
        var count = Math.Max(1, (totalTicks - tolerance + length - 1) / length);
        var starts = new long[count];
        for (var i = 0; i < count; i++)
        {
            starts[i] = i * length;
        }

        return new SegmentPlan(SegmentPlanKind.Uniform, starts, totalTicks, frameTicks, []);
    }

    private static long GetFrameTicks(double? frameRate)
        => frameRate is >= 1 and <= 1000 ? (long)Math.Round(TimeSpan.TicksPerSecond / frameRate.Value) : 0;
}
