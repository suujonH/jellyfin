using System;
using System.Linq;
using Jellyfin.MediaEncoding.Hls.Plans;
using Jellyfin.MediaEncoding.Keyframes;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Plans;

public class HlsCutsTests
{
    private const double Fps = 24000d / 1001;

    private static readonly long _length = Seconds(6);

    // Boundaries at 0, 6.1, 12.0, 19.8, 25.0, 31.3 and 38.4.
    private static readonly SegmentPlan _keyframePlan = SegmentPlanner.CreateKeyframePlan(
        new KeyframeData(Seconds(40), new[] { 0, 1.7, 4.2, 6.1, 9.9, 12.0, 13.5, 19.8, 20.2, 25.0, 31.3, 32.0, 38.4 }.Select(Seconds).ToList()),
        Seconds(40),
        _length,
        Fps)!;

    private static readonly SegmentPlan _uniformPlan = SegmentPlanner.CreateUniformPlan(Seconds(40), _length, Fps);

    [Theory]
    [InlineData(HlsCutMode.CopyVideo)]
    [InlineData(HlsCutMode.TranscodeVideo)]
    [InlineData(HlsCutMode.CopyAudio)]
    [InlineData(HlsCutMode.TranscodeAudio)]
    public void GetRunStart_FirstSegment_ReadsFromTheStart(HlsCutMode mode)
    {
        var plan = mode == HlsCutMode.CopyVideo ? _keyframePlan : _uniformPlan;

        Assert.Equal(new HlsRunStart(0, 0, 0), HlsCuts.GetRunStart(plan, 0, mode));
    }

    [Fact]
    public void GetRunStart_CopyVideo_SeeksASecondBeforeThePrecedingKeyframeAndDropsTheFirstFile()
    {
        Assert.Equal(Seconds(12.0), _keyframePlan.Segments[2].StartTicks);

        // The keyframe before 12.0 is 9.9.
        Assert.Equal(new HlsRunStart(Seconds(8.9), 0, null), HlsCuts.GetRunStart(_keyframePlan, 2, HlsCutMode.CopyVideo));
    }

    [Fact]
    public void GetRunStart_CopyVideoNearTheStart_ReadsFromTheStartAndKeepsTheFirstFile()
    {
        var plan = SegmentPlanner.CreateKeyframePlan(new KeyframeData(Seconds(20), [0, Seconds(0.8), Seconds(6), Seconds(12)]), Seconds(20), _length, Fps)!;

        Assert.Equal(new HlsRunStart(0, 0, 0), HlsCuts.GetRunStart(plan, 1, HlsCutMode.CopyVideo));
    }

    [Fact]
    public void GetRunStart_CopyVideoWithUniformPlan_Throws()
    {
        Assert.Throws<ArgumentException>(() => HlsCuts.GetRunStart(_uniformPlan, 1, HlsCutMode.CopyVideo));
    }

    [Fact]
    public void GetRunStart_TranscodeVideo_SeeksHalfAFrameEarly()
    {
        var boundary = _uniformPlan.Segments[3].StartTicks;

        Assert.Equal(new HlsRunStart(boundary - _uniformPlan.ToleranceTicks, 0, boundary), HlsCuts.GetRunStart(_uniformPlan, 3, HlsCutMode.TranscodeVideo));
    }

    [Fact]
    public void GetRunStart_CopyAudio_TrimsAtTheOutput()
    {
        var plan = SegmentPlanner.CreateUniformPlan(Seconds(40), _length, null);

        Assert.Equal(new HlsRunStart(Seconds(10), Seconds(12), Seconds(12)), HlsCuts.GetRunStart(plan, 2, HlsCutMode.CopyAudio));
    }

    [Fact]
    public void GetRunStart_TranscodeAudio_SeeksToTheBoundary()
    {
        var plan = SegmentPlanner.CreateUniformPlan(Seconds(40), _length, null);

        Assert.Equal(new HlsRunStart(Seconds(12), 0, Seconds(12)), HlsCuts.GetRunStart(plan, 2, HlsCutMode.TranscodeAudio));
    }

    [Fact]
    public void GetForcedKeyframeTicks_FromTheStart_LeavesOutZeroAndEndsAtTheNextBoundary()
    {
        var forced = HlsCuts.GetForcedKeyframeTicks(_uniformPlan, 0, 3);

        Assert.Equal(Enumerable.Range(1, 3).Select(k => _uniformPlan.Segments[k].StartTicks - _uniformPlan.ToleranceTicks), forced);
    }

    [Fact]
    public void GetForcedKeyframeTicks_ToTheEnd_StopsAtTheLastBoundary()
    {
        var forced = HlsCuts.GetForcedKeyframeTicks(_uniformPlan, 4, _uniformPlan.Segments.Count);

        Assert.Equal(Enumerable.Range(4, 3).Select(k => _uniformPlan.Segments[k].StartTicks - _uniformPlan.ToleranceTicks), forced);
    }

    [Theory]
    [InlineData(0, "0")]
    [InlineData(60_060_000, "6.006")]
    [InlineData(12_345_678, "1.234568")]
    public void FormatSeconds_Ticks_PrintsMicroseconds(long ticks, string expected)
    {
        Assert.Equal(expected, HlsCuts.FormatSeconds(ticks));
    }

    private static long Seconds(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
}
