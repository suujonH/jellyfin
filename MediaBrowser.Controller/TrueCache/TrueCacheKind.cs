namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// The kind of resource a <c>__true_cache__</c> entry describes.
/// </summary>
public enum TrueCacheKind
{
    /// <summary>A video file, including <c>.strm</c> shortcuts.</summary>
    Video,

    /// <summary>An audio file.</summary>
    Audio,

    /// <summary>A DVD folder.</summary>
    Dvd,

    /// <summary>A Blu-ray folder.</summary>
    BluRay,

    /// <summary>A book or comic.</summary>
    Book,

    /// <summary>A photo.</summary>
    Photo,

    /// <summary>The album-wide values of an album folder.</summary>
    Album
}
