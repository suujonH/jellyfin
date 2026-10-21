using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.MediaEncoding.Hls.Plans;
using Jellyfin.MediaEncoding.Hls.Sessions;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Sessions;

public class HlsSegmentLocatorTests
{
    // Segments of 6 s at 25 fps, from 0 to 30 s; half a frame is 20 ms.
    private static readonly SegmentPlan _plan = SegmentPlanner.CreateUniformPlan(Seconds(30), Seconds(6), 25);

    private static readonly long _tolerance = _plan.ToleranceTicks;

    [Fact]
    public void Locate_NoLines_IsPendingWhileRunningAndAbsentOnceFinished()
    {
        Assert.Equal(HlsSegmentState.Pending, Locate(1, 0, [], false).State);
        Assert.Equal(HlsSegmentState.Absent, Locate(1, 0, [], true).State);
    }

    [Fact]
    public void Locate_LaterLine_CompletesTheSegment()
    {
        var location = Locate(0, 0, Entries(0, 3, 6, 12), false);

        Assert.Equal(new HlsSegmentLocation(HlsSegmentState.Complete, 0, 2), location);
    }

    [Fact]
    public void Locate_LastLineReachingTheNextBoundary_WaitsForTheNextFile()
    {
        List<HlsListEntry> entries = [Entry(0, 0, Seconds(6))];

        Assert.Equal(HlsSegmentState.Pending, Locate(0, 0, entries, false, _ => false).State);
        Assert.Equal(HlsSegmentState.Complete, Locate(0, 0, entries, false, number => number == 1).State);
    }

    [Fact]
    public void Locate_LastLineShortOfTheNextBoundary_IsPending()
    {
        // The next file may still start before the boundary.
        Assert.Equal(HlsSegmentState.Pending, Locate(0, 0, Entries(0, 4), false, _ => true).State);
    }

    [Fact]
    public void Locate_FinishedRun_CompletesItsLastSegment()
    {
        var entries = Entries(0, 6, 12, 18, 24);

        Assert.Equal(HlsSegmentState.Pending, Locate(4, 0, entries, false, _ => true).State);
        Assert.Equal(new HlsSegmentLocation(HlsSegmentState.Complete, 4, 1), Locate(4, 0, entries, true));
    }

    [Fact]
    public void Locate_UnknownFirstFile_IsNoSegments()
    {
        // A copy restarted for segment 2 seeks before the keyframe at 9.9 s; its first file starts there or earlier.
        var entries = Entries(0, 9.9, 12, 18);

        Assert.Equal(new HlsSegmentLocation(HlsSegmentState.Complete, 2, 1), Locate(2, null, entries, false));
    }

    [Fact]
    public void Locate_FirstFileAtTheBoundaryOfTheRun_IsItsFirstSegment()
    {
        // The list gives no start for the first file of a run; a transcode starts it at its boundary.
        var location = Locate(2, Seconds(12), Entries(0, 18, 24), false);

        Assert.Equal(new HlsSegmentLocation(HlsSegmentState.Complete, 0, 1), location);
    }

    [Fact]
    public void Locate_FileWithinTheToleranceOfABoundary_StartsIt()
    {
        var entries = new List<HlsListEntry>
        {
            Entry(0, 0, Seconds(6) - _tolerance),
            Entry(1, Seconds(6) - _tolerance, Seconds(12) - _tolerance - 1),
            Entry(2, Seconds(12) - _tolerance - 1, Seconds(18)),
            Entry(3, Seconds(18), Seconds(24))
        };

        Assert.Equal(new HlsSegmentLocation(HlsSegmentState.Complete, 1, 2), Locate(1, 0, entries, false));
    }

    [Fact]
    public void Locate_FileStartingLate_IsTheSegment()
    {
        // The keyframe of 12 s is missing; the content up to 13 s ends segment 1.
        var entries = Entries(0, 6, 13, 18);

        Assert.Equal(new HlsSegmentLocation(HlsSegmentState.Complete, 1, 1), Locate(1, 0, entries, false));
        Assert.Equal(new HlsSegmentLocation(HlsSegmentState.Complete, 2, 1), Locate(2, 0, entries, false));
    }

    [Fact]
    public void Locate_NoFileInTheSegment_IsTheFileAcrossIt()
    {
        // The file from 6 s to 18 s holds segment 2 as well as segment 1.
        var entries = Entries(0, 6, 18);

        Assert.Equal(new HlsSegmentLocation(HlsSegmentState.Complete, 1, 1), Locate(2, 0, entries, false));
        Assert.Equal(new HlsSegmentLocation(HlsSegmentState.Complete, 1, 1), Locate(2, 0, entries, true));
        Assert.Equal(HlsSegmentState.Pending, Locate(2, 0, Entries(0, 6), false).State);
    }

    [Fact]
    public void Locate_FinishedRunEndingInASegmentWithoutAFile_EndsWithTheLastFile()
    {
        List<HlsListEntry> entries = [Entry(0, 0, Seconds(6)), Entry(1, Seconds(6), Seconds(20))];

        Assert.Equal(new HlsSegmentLocation(HlsSegmentState.Complete, 1, 1), Locate(3, 0, entries, true));
        Assert.Equal(HlsSegmentState.Absent, Locate(4, 0, entries, true).State);
    }

    [Fact]
    public void Locate_RunStartingAfterTheSegment_IsSkipped()
    {
        Assert.Equal(HlsSegmentState.Skipped, Locate(1, Seconds(12), Entries(0, 18), false).State);
    }

    private static HlsSegmentLocation Locate(int index, long? firstFileStartTicks, IReadOnlyList<HlsListEntry> entries, bool finished, Func<int, bool>? fileExists = null)
        => HlsSegmentLocator.Locate(_plan, index, _tolerance, firstFileStartTicks, entries, finished, fileExists ?? (_ => false));

    // Files starting at the given seconds, each ending where the next starts; the last ends 1 s later, before the next boundary.
    private static List<HlsListEntry> Entries(params double[] starts)
        => starts.Select((start, i) => Entry(i, i == 0 ? 0 : Seconds(start), i + 1 < starts.Length ? Seconds(starts[i + 1]) : Seconds(start + 1))).ToList();

    private static HlsListEntry Entry(int number, long start, long end) => new($"run-{number}.ts", start, end);

    private static long Seconds(double value) => (long)Math.Round(value * TimeSpan.TicksPerSecond);
}
