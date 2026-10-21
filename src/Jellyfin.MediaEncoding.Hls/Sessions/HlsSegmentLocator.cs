using System;
using System.Collections.Generic;
using Jellyfin.MediaEncoding.Hls.Plans;

namespace Jellyfin.MediaEncoding.Hls.Sessions;

/// <summary>
/// Finds the GOP files of a plan segment in the list of a run. The segment is the files of the run whose starts fall in
/// [B_k − tol, B_{k+1} − tol), the last segment's range being open; a run's first file starts where its
/// <see cref="HlsRunStart.FirstFileStartTicks"/> says, and is no segment's when that is unknown.
/// </summary>
/// <remarks>
/// A file may start late: the keyframes of a copy are not those of the plan, or an encoder that cuts by GOP length started
/// off the grid. It still belongs to the segment its start falls in, so that the segments stay contiguous and the same in
/// every run; only their durations differ from the playlist. A segment without a file of its own lies inside the file
/// across it, which is then sent for it as well.
/// </remarks>
internal static class HlsSegmentLocator
{
    /// <summary>
    /// Locates a plan segment in the list of a run.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="index">The segment.</param>
    /// <param name="toleranceTicks">How far a file may start from a boundary and still start at it.</param>
    /// <param name="firstFileStartTicks">Where the first file of the run starts, or null to leave it out.</param>
    /// <param name="entries">The list read so far.</param>
    /// <param name="finished">Whether ffmpeg ended the run itself, so that the list is whole and every file closed.</param>
    /// <param name="fileExists">Gets whether the file of a list index has been created.</param>
    /// <returns>Where the segment lies.</returns>
    public static HlsSegmentLocation Locate(
        SegmentPlan plan,
        int index,
        long toleranceTicks,
        long? firstFileStartTicks,
        IReadOnlyList<HlsListEntry> entries,
        bool finished,
        Func<int, bool> fileExists)
    {
        var boundary = plan.Segments[index].StartTicks;
        var low = boundary - toleranceTicks;
        var isLast = index + 1 == plan.Segments.Count;
        var high = isLast ? long.MaxValue : plan.Segments[index + 1].StartTicks - toleranceTicks;

        var first = -1;
        var last = -1;
        var next = -1;
        for (var i = 0; i < entries.Count; i++)
        {
            long start;
            if (i > 0)
            {
                start = entries[i].StartTicks;
            }
            else if (firstFileStartTicks.HasValue)
            {
                start = firstFileStartTicks.Value;
            }
            else
            {
                continue;
            }

            if (start < low)
            {
                continue;
            }

            if (start >= high)
            {
                next = i;
                break;
            }

            if (first < 0)
            {
                first = i;
            }

            last = i;
        }

        if (first < 0)
        {
            return LocateAcross(boundary, toleranceTicks, entries, next, finished);
        }

        // The muxer creates a file after it closes the one before, and writes its line before closing it. A later line,
        // or a later file once the files so far reach the next boundary, closes the segment; for the last segment only
        // the end of the run does.
        var complete = next >= 0
            || finished
            || (!isLast && entries[last].EndTicks >= high && fileExists(last + 1));
        return new HlsSegmentLocation(complete ? HlsSegmentState.Complete : HlsSegmentState.Pending, first, last - first + 1);
    }

    // A segment no file starts in: the file before the next one, or the last file of a finished run, spans it. The next
    // file is the first one starting after the segment, or -1 for none yet.
    private static HlsSegmentLocation LocateAcross(long boundary, long toleranceTicks, IReadOnlyList<HlsListEntry> entries, int next, bool finished)
    {
        if (next == 0)
        {
            // The run starts after the segment.
            return new HlsSegmentLocation(HlsSegmentState.Skipped, -1, 0);
        }

        if (next > 0)
        {
            return new HlsSegmentLocation(HlsSegmentState.Complete, next - 1, 1);
        }

        if (!finished)
        {
            return new HlsSegmentLocation(HlsSegmentState.Pending, -1, 0);
        }

        // The media ends in the segment, or before it.
        return entries.Count > 0 && entries[^1].EndTicks > boundary + toleranceTicks
            ? new HlsSegmentLocation(HlsSegmentState.Complete, entries.Count - 1, 1)
            : new HlsSegmentLocation(HlsSegmentState.Absent, -1, 0);
    }
}
