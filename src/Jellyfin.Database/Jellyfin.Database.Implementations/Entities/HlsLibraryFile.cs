using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// A file an HLS worker wrote into a library folder through the server.
/// </summary>
public class HlsLibraryFile
{
    /// <summary>
    /// Gets or sets the id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the library the file was written to.
    /// </summary>
    public Guid LibraryId { get; set; }

    /// <summary>
    /// Gets or sets the full path of the file on the server.
    /// </summary>
    public required string Path { get; set; }

    /// <summary>
    /// Gets or sets the size of the file, in bytes.
    /// </summary>
    public long Length { get; set; }

    /// <summary>
    /// Gets or sets the worker that wrote the file.
    /// </summary>
    public Guid? WorkerId { get; set; }

    /// <summary>
    /// Gets or sets when the file was written.
    /// </summary>
    public DateTime DateCreated { get; set; }
}
