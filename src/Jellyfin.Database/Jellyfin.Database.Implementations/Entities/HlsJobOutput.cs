using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// A rendition a worker reported for a job; once published, the list of its files.
/// </summary>
public class HlsJobOutput
{
    /// <summary>
    /// Gets or sets the job.
    /// </summary>
    public Guid JobId { get; set; }

    /// <summary>
    /// Gets or sets the key of the rendition.
    /// </summary>
    public required string Key { get; set; }

    /// <summary>
    /// Gets or sets the directory, from the plan of the job, relative to the root of the HLS store.
    /// </summary>
    public required string RelativeDirectory { get; set; }

    /// <summary>
    /// Gets or sets the summary, a <c>MediaBrowser.Model.Hls.HlsRenditionSummary</c> as JSON.
    /// </summary>
    public required string Summary { get; set; }

    /// <summary>
    /// Gets or sets the files, <c>MediaBrowser.Model.Hls.HlsOutputFile</c>s as JSON.
    /// </summary>
    public required string Files { get; set; }

    /// <summary>
    /// Gets or sets the duration of every segment in seconds, as JSON; empty for subtitles and previews.
    /// </summary>
    public required string Segments { get; set; }
}
