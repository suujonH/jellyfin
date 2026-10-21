namespace MediaBrowser.Model.Hls;

/// <summary>
/// What a rendition of pre-encoded HLS holds.
/// </summary>
public enum HlsRenditionKind
{
    /// <summary>
    /// A video quality.
    /// </summary>
    Video = 0,

    /// <summary>
    /// An audio track.
    /// </summary>
    Audio = 1,

    /// <summary>
    /// A text subtitle track, as WebVTT.
    /// </summary>
    Subtitle = 2,

    /// <summary>
    /// A preview picture.
    /// </summary>
    Preview = 3
}
