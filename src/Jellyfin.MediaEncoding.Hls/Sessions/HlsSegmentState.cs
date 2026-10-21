namespace Jellyfin.MediaEncoding.Hls.Sessions;

/// <summary>
/// What a run has produced of a plan segment.
/// </summary>
internal enum HlsSegmentState
{
    /// <summary>
    /// The run has not yet written all the files of the segment.
    /// </summary>
    Pending,

    /// <summary>
    /// The files of the segment are written and closed.
    /// </summary>
    Complete,

    /// <summary>
    /// The run starts after the segment.
    /// </summary>
    Skipped,

    /// <summary>
    /// The run ended before the segment.
    /// </summary>
    Absent
}
