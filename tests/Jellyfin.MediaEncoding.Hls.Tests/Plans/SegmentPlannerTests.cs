using System;
using System.Linq;
using Jellyfin.MediaEncoding.Hls.Plans;
using Jellyfin.MediaEncoding.Keyframes;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Plans;

public class SegmentPlannerTests
{
    private const double Fps = 24000d / 1001;

    private static readonly long _length = Seconds(6);

    [Fact]
    public void CreateKeyframePlan_RegularKeyframes_CutsEverySegmentLength()
    {
        var plan = KeyframePlan(20, Enumerable.Range(0, 10).Select(i => i * 2.0).ToArray());

        Assert.Equal(SegmentPlanKind.Keyframe, plan.Kind);
        Assert.Equal([0, Seconds(6), Seconds(12), Seconds(18)], plan.Segments.Select(s => s.StartTicks));
        Assert.Equal(Seconds(2), plan.Segments[^1].DurationTicks);
        Assert.Equal(Seconds(20), plan.TotalTicks);
    }

    [Fact]
    public void CreateKeyframePlan_KeyframesAfterALongGop_DoNotMakeShortSegments()
    {
        // Cutting at the first keyframe after every multiple of the length would cut at 10 and again at 12.
        var plan = KeyframePlan(30, [0, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25]);

        Assert.Equal([0, Seconds(10), Seconds(16), Seconds(22)], plan.Segments.Select(s => s.StartTicks));
    }

    [Fact]
    public void CreateKeyframePlan_CloserKeyframeBeforeTheTarget_IsTaken()
    {
        var plan = KeyframePlan(42, Enumerable.Range(0, 9).Select(i => i * 5.0).ToArray());

        Assert.All(plan.Segments.SkipLast(1), s => Assert.Equal(Seconds(5), s.DurationTicks));
    }

    [Fact]
    public void CreateKeyframePlan_EndCloserThanTheLastKeyframe_JoinsTheRemainder()
    {
        var plan = KeyframePlan(7, [0, 3.5]);

        Assert.Single(plan.Segments);
    }

    [Fact]
    public void CreateKeyframePlan_UnsortedDuplicateKeyframesPastTheEnd_AreIgnored()
    {
        var plan = KeyframePlan(13, [12, 6, 0, 6, 15]);

        Assert.Equal([0, Seconds(6), Seconds(12)], plan.Segments.Select(s => s.StartTicks));
        Assert.Equal([Seconds(6), Seconds(12)], plan.Keyframes);
    }

    [Fact]
    public void CreateKeyframePlan_FirstKeyframeAfterZero_StartsAtZero()
    {
        // A video that starts after the audio: a run from the start opens its first file at 0 all the same.
        var plan = KeyframePlan(20, [0.3, 6.3, 12.3, 18.3]);

        Assert.Equal([0, Seconds(6.3), Seconds(12.3), Seconds(18.3)], plan.Segments.Select(s => s.StartTicks));
    }

    [Fact]
    public void CreateKeyframePlan_IntraOnly_IsRejected()
    {
        Assert.Null(SegmentPlanner.CreateKeyframePlan(Keyframes(60, Enumerable.Range(0, 1440).Select(i => i / Fps).ToArray()), Seconds(60), _length, Fps));
    }

    [Fact]
    public void CreateKeyframePlan_DuplicateKeyframes_CountOnce()
    {
        // 0.48 s GOPs, every one listed twice.
        var times = Enumerable.Range(0, 125).Select(i => i * 0.48).ToArray();
        var plan = SegmentPlanner.CreateKeyframePlan(Keyframes(60, [.. times, .. times]), Seconds(60), _length, Fps);

        Assert.NotNull(plan);
        Assert.Equal(SegmentPlanner.CreateKeyframePlan(Keyframes(60, times), Seconds(60), _length, Fps)!.Segments, plan.Segments);
    }

    [Fact]
    public void CreateKeyframePlan_KeyframesTooFarApart_IsRejected()
    {
        Assert.Null(SegmentPlanner.CreateKeyframePlan(Keyframes(100, [0, 50]), Seconds(100), _length, Fps));
    }

    [Fact]
    public void CreateKeyframePlan_NoKeyframes_IsRejected()
    {
        Assert.Null(SegmentPlanner.CreateKeyframePlan(Keyframes(100, []), Seconds(100), _length, Fps));
    }

    [Fact]
    public void CreateUniformPlan_FractionalFrameRate_LengthIsWholeFrames()
    {
        var plan = SegmentPlanner.CreateUniformPlan(Seconds(40), _length, Fps);

        // 144 frames of 1001/24000 s.
        Assert.Equal(SegmentPlanKind.Uniform, plan.Kind);
        Assert.Equal(7, plan.Segments.Count);
        Assert.All(plan.Segments.SkipLast(1), s => Assert.Equal(60_060_000, s.DurationTicks));
        Assert.Equal(Seconds(40) - (6 * 60_060_000), plan.Segments[^1].DurationTicks);
        Assert.Equal(7, plan.TargetDurationSeconds);
        Assert.Equal(208_541, plan.ToleranceTicks);
    }

    [Fact]
    public void CreateUniformPlan_WholeFrameRate_KeepsTheLength()
    {
        var plan = SegmentPlanner.CreateUniformPlan(Seconds(60), _length, 30);

        Assert.All(plan.Segments, s => Assert.Equal(_length, s.DurationTicks));
        Assert.Equal(6, plan.TargetDurationSeconds);
    }

    [Fact]
    public void CreateUniformPlan_RemainderWithinTheTolerance_JoinsTheLastSegment()
    {
        var plan = SegmentPlanner.CreateUniformPlan(Seconds(12.001), _length, null);

        Assert.Equal(2, plan.Segments.Count);
        Assert.Equal(Seconds(6.001), plan.Segments[^1].DurationTicks);
    }

    [Fact]
    public void CreateUniformPlan_ShorterThanASegment_IsOneSegment()
    {
        var plan = SegmentPlanner.CreateUniformPlan(Seconds(2), _length, null);

        Assert.Equal(Seconds(2), Assert.Single(plan.Segments).DurationTicks);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(5.9, 0)]
    [InlineData(6, 1)]
    [InlineData(17, 2)]
    [InlineData(100, 3)]
    public void IndexAt_Time_GivesItsSegment(double seconds, int index)
    {
        var plan = SegmentPlanner.CreateUniformPlan(Seconds(20), _length, null);

        Assert.Equal(index, plan.IndexAt(Seconds(seconds)));
    }

    private static SegmentPlan KeyframePlan(double total, double[] keyframes)
        => SegmentPlanner.CreateKeyframePlan(Keyframes(total, keyframes), Seconds(total), _length, Fps) ?? throw new InvalidOperationException("No plan");

    private static KeyframeData Keyframes(double total, double[] keyframes) => new(Seconds(total), keyframes.Select(Seconds).ToList());

    private static long Seconds(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);
}
