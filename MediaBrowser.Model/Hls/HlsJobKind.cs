namespace MediaBrowser.Model.Hls;

/// <summary>
/// What a job of pre-encoded HLS starts from.
/// </summary>
public enum HlsJobKind
{
    /// <summary>
    /// A library file whose content is not identified yet.
    /// </summary>
    Source = 0,

    /// <summary>
    /// Content that is identified and has renditions to encode.
    /// </summary>
    Media = 1
}
