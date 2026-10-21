namespace Jellyfin.MediaEncoding.Hls.Plans;

/// <summary>
/// What decides the cuts of a live HLS run.
/// </summary>
public enum HlsCutMode
{
    /// <summary>
    /// The video is copied; the run opens a file at every source keyframe. Needs a keyframe plan.
    /// </summary>
    CopyVideo,

    /// <summary>
    /// The video is transcoded with a keyframe forced at every boundary, so every segment is one file.
    /// </summary>
    TranscodeVideo,

    /// <summary>
    /// Audio alone, copied; the run cuts at the first packet after every segment length from its first packet.
    /// </summary>
    CopyAudio,

    /// <summary>
    /// Audio alone, transcoded; cut like <see cref="CopyAudio"/>.
    /// </summary>
    TranscodeAudio
}
