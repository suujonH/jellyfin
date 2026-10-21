using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// Content of pre-encoded HLS, identified by its hash; files with equal content share it.
/// </summary>
public class HlsMedia
{
    /// <summary>
    /// Gets or sets the SHA-256 hash of the content, in lower case.
    /// </summary>
    public required string Sha256 { get; set; }

    /// <summary>
    /// Gets or sets the directory of the content in the HLS store: the first 32 characters of the hash.
    /// </summary>
    public required string MediaKey { get; set; }

    /// <summary>
    /// Gets or sets the library the content was first found in.
    /// </summary>
    public Guid LibraryId { get; set; }

    /// <summary>
    /// Gets or sets the probe of the content, a <c>MediaBrowser.Model.Hls.HlsProbe</c> as JSON; it never changes.
    /// </summary>
    public required string Probe { get; set; }

    /// <summary>
    /// Gets or sets when the content was first found without a library file; null while a file has it.
    /// </summary>
    public DateTime? DateOrphaned { get; set; }

    /// <summary>
    /// Gets or sets when the content was identified.
    /// </summary>
    public DateTime DateCreated { get; set; }
}
