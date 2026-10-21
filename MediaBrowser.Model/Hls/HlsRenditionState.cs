namespace MediaBrowser.Model.Hls;

/// <summary>
/// The state of a rendition of pre-encoded HLS.
/// </summary>
public enum HlsRenditionState
{
    /// <summary>
    /// The rendition is not published.
    /// </summary>
    Missing = 0,

    /// <summary>
    /// The rendition is published.
    /// </summary>
    Published = 2,

    /// <summary>
    /// Encoding the rendition failed too often; it waits for a rebuild of the work list.
    /// </summary>
    Failed = 3
}
