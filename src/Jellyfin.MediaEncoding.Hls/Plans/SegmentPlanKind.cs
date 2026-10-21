namespace Jellyfin.MediaEncoding.Hls.Plans;

/// <summary>
/// How the boundaries of a <see cref="SegmentPlan"/> were chosen.
/// </summary>
public enum SegmentPlanKind
{
    /// <summary>
    /// The boundaries are keyframes of the source, so a copy of the video can be cut at them.
    /// </summary>
    Keyframe,

    /// <summary>
    /// The boundaries are one segment length apart; a transcode forces keyframes at them, audio is cut at the packet that follows them.
    /// </summary>
    Uniform
}
