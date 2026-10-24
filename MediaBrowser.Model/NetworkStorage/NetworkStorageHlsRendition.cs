using MediaBrowser.Model.Hls;

namespace MediaBrowser.Model.NetworkStorage;

/// <summary>
/// A published rendition of the pre-encoded HLS of a resource.
/// </summary>
public class NetworkStorageHlsRendition
{
    /// <summary>
    /// Gets or sets the key, such as <c>v1080</c>.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the kind.
    /// </summary>
    public HlsRenditionKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the video quality, such as 1080; 0 for other kinds.
    /// </summary>
    public int Quality { get; set; }

    /// <summary>
    /// Gets or sets the language of an audio or subtitle track.
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the title of an audio or subtitle track.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the number of audio channels.
    /// </summary>
    public int Channels { get; set; }

    /// <summary>
    /// Gets or sets the size of its files, in bytes.
    /// </summary>
    public long Size { get; set; }

    /// <summary>
    /// Gets or sets the number of its files.
    /// </summary>
    public int FileCount { get; set; }

    /// <summary>
    /// Gets or sets the root of the HLS store it is in.
    /// </summary>
    public string Location { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the key of the storage it is in; <c>null</c> when it is in no network storage.
    /// </summary>
    public string? StorageKey { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether it is in the storage listed.
    /// </summary>
    public bool InStorage { get; set; }
}
