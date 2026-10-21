using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// A library file that pre-encoded HLS is made from.
/// </summary>
public class HlsSource
{
    /// <summary>
    /// Gets or sets the id of the item.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the id of the library the item is in.
    /// </summary>
    public Guid LibraryId { get; set; }

    /// <summary>
    /// Gets or sets the path of the file, or the target its <c>.strm</c> file names.
    /// </summary>
    public required string Path { get; set; }

    /// <summary>
    /// Gets or sets the size of the media, in bytes, or 0 when it is not known.
    /// </summary>
    public long Length { get; set; }

    /// <summary>
    /// Gets or sets when the file was last modified.
    /// </summary>
    public DateTime DateModified { get; set; }

    /// <summary>
    /// Gets or sets the SHA-256 hash of the content, in lower case; null until a worker identified the file as it is now.
    /// </summary>
    public string? Sha256 { get; set; }
}
