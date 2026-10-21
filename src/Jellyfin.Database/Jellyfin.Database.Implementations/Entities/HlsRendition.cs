using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// A rendition of pre-encoded HLS: a video quality, an audio or subtitle track, or the preview of a content.
/// </summary>
public class HlsRendition
{
    /// <summary>
    /// Gets or sets the hash of the content.
    /// </summary>
    public required string Sha256 { get; set; }

    /// <summary>
    /// Gets or sets the key, such as <c>v1080</c>, <c>a1</c>, <c>s2</c> or <c>preview</c>.
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// Gets or sets the state, a <c>MediaBrowser.Model.Hls.HlsRenditionState</c>.
    /// </summary>
    public int State { get; set; }

    /// <summary>
    /// Gets or sets the job that encodes the rendition, or the failed job that keeps a published rendition from being scheduled.
    /// </summary>
    public Guid? JobId { get; set; }

    /// <summary>
    /// Gets or sets the job whose output is published; its <see cref="HlsJobOutput"/> lists the files.
    /// </summary>
    public Guid? PublishedJobId { get; set; }

    /// <summary>
    /// Gets or sets the published directory, relative to the root of the HLS store.
    /// </summary>
    public string? RelativeDirectory { get; set; }

    /// <summary>
    /// Gets or sets the summary of the published output, a <c>MediaBrowser.Model.Hls.HlsRenditionSummary</c> as JSON.
    /// </summary>
    public string? Summary { get; set; }

    /// <summary>
    /// Gets or sets the encoding profile the rendition was published under; kept when it is retired.
    /// </summary>
    public string? Profile { get; set; }

    /// <summary>
    /// Gets or sets when the rendition was published.
    /// </summary>
    public DateTime? DatePublished { get; set; }
}
