using System;
using System.Collections.Generic;
using System.Globalization;

namespace Jellyfin.MediaEncoding.Hls.Plans;

/// <summary>
/// Where live HLS runs start and where they force keyframes. The rules are pinned by the tests in <c>Jellyfin.MediaEncoding.Hls.Tests/Ffmpeg</c>.
/// </summary>
public static class HlsCuts
{
    // A copy seeks a second before the keyframe that precedes its boundary: MPEG-TS has no index, so the seek lands on any packet
    // and the copy starts at the next keyframe, which then still lies before the boundary.
    private static readonly long _copyVideoLeadTicks = TimeSpan.FromSeconds(1).Ticks;

    // A copy of audio alone reads from two seconds early and trims at the output.
    private static readonly long _copyAudioLeadTicks = TimeSpan.FromSeconds(2).Ticks;

    /// <summary>
    /// Gets how a run starts to produce a segment and the ones after it.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="index">The first segment of the run.</param>
    /// <param name="mode">What decides the cuts.</param>
    /// <returns>The start of the run.</returns>
    public static HlsRunStart GetRunStart(SegmentPlan plan, int index, HlsCutMode mode)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, plan.Segments.Count);
        if (mode == HlsCutMode.CopyVideo && plan.Kind != SegmentPlanKind.Keyframe)
        {
            throw new ArgumentException("Copying the video needs a keyframe plan.", nameof(plan));
        }

        if (index == 0)
        {
            return new HlsRunStart(0, 0, 0);
        }

        var boundary = plan.Segments[index].StartTicks;
        switch (mode)
        {
            case HlsCutMode.CopyVideo:
                // The boundary then opens a later file of the run, whose start the list records. Without a seek the run
                // starts at the start of the stream, and so does its first file.
                var seek = plan.GetKeyframeBefore(boundary) - _copyVideoLeadTicks;
                return seek > 0 ? new HlsRunStart(seek, 0, null) : new HlsRunStart(0, 0, 0);
            case HlsCutMode.TranscodeVideo:
                // An exact seek half a frame early starts at the frame closest to the boundary.
                return new HlsRunStart(boundary - plan.ToleranceTicks, 0, boundary);
            case HlsCutMode.CopyAudio:
                return new HlsRunStart(Math.Max(0, boundary - _copyAudioLeadTicks), boundary, boundary);
            case HlsCutMode.TranscodeAudio:
                return new HlsRunStart(boundary, 0, boundary);
            default:
                throw new ArgumentOutOfRangeException(nameof(mode), mode, null);
        }
    }

    /// <summary>
    /// Gets the times a transcode run forces keyframes at: half a frame before every boundary from its first segment to the
    /// boundary after its last, so each lands on the frame closest to its boundary. The start of the stream is left out.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="startIndex">The first segment of the run.</param>
    /// <param name="endIndex">The segment after the last one of the run.</param>
    /// <returns>The times on the plan timeline, in order.</returns>
    public static IReadOnlyList<long> GetForcedKeyframeTicks(SegmentPlan plan, int startIndex, int endIndex)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentOutOfRangeException.ThrowIfNegative(startIndex);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(endIndex, startIndex);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(endIndex, plan.Segments.Count);

        var result = new List<long>();
        for (var k = Math.Max(startIndex, 1); k <= endIndex && k < plan.Segments.Count; k++)
        {
            result.Add(plan.Segments[k].StartTicks - plan.ToleranceTicks);
        }

        return result;
    }

    /// <summary>
    /// Formats a time as seconds for an ffmpeg argument, to the microsecond, the precision of the segment list.
    /// </summary>
    /// <param name="ticks">The time.</param>
    /// <returns>The seconds.</returns>
    public static string FormatSeconds(long ticks)
        => ((decimal)ticks / TimeSpan.TicksPerSecond).ToString("0.######", CultureInfo.InvariantCulture);
}
