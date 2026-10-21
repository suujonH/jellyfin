using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// A rendition directory of the HLS store waiting to be deleted.
/// </summary>
public class HlsDeletion
{
    /// <summary>
    /// Gets or sets the directory, relative to its root; the lease token in it makes it unique across the roots.
    /// </summary>
    public required string RelativeDirectory { get; set; }

    /// <summary>
    /// Gets or sets the root of the HLS store the directory is in: an absolute local path, or <c>s3://bucket/prefix</c>.
    /// </summary>
    public required string Base { get; set; }

    /// <summary>
    /// Gets or sets when the directory may be deleted.
    /// </summary>
    public DateTime NotBefore { get; set; }

    /// <summary>
    /// Gets or sets how often deleting the directory failed.
    /// </summary>
    public int Attempts { get; set; }

    /// <summary>
    /// Gets or sets why the directory is deleted.
    /// </summary>
    public required string Reason { get; set; }

    /// <summary>
    /// Gets or sets the last error.
    /// </summary>
    public string? LastError { get; set; }
}
