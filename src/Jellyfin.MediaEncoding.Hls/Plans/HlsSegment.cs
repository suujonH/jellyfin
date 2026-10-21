namespace Jellyfin.MediaEncoding.Hls.Plans;

/// <summary>
/// One segment of a <see cref="SegmentPlan"/>.
/// </summary>
/// <param name="Index">The index of the segment, from 0.</param>
/// <param name="StartTicks">The start of the segment on the plan timeline, in ticks.</param>
/// <param name="DurationTicks">The duration of the segment in ticks.</param>
public sealed record HlsSegment(int Index, long StartTicks, long DurationTicks)
{
    /// <summary>
    /// Gets the end of the segment, which is the start of the next one.
    /// </summary>
    public long EndTicks => StartTicks + DurationTicks;
}
