using System;

namespace MediaBrowser.Model.NetworkStorage;

/// <summary>
/// Statistics of the resources the library has in one storage: an S3 bucket, an http host.
/// They are taken from what the library already knows; nothing is read from the storage.
/// </summary>
public class NetworkStorageSummary
{
    /// <summary>
    /// Gets or sets the key of the storage, such as <c>s3:bucket</c> or <c>http:example.com</c>.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the protocol, one of <see cref="NetworkStorageProtocols"/>, or empty for resources whose address is not known.
    /// </summary>
    public string Protocol { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the bucket or host.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the id of the access that covers the storage.
    /// </summary>
    public Guid? AccessId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the access that covers the storage makes it an archive.
    /// </summary>
    public bool IsArchive { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the storage can only be read with an access.
    /// </summary>
    public bool RequiresAccess { get; set; }

    /// <summary>
    /// Gets or sets the number of resources: movies, episodes and tracks, not seasons or HLS renditions.
    /// </summary>
    public int ResourceCount { get; set; }

    /// <summary>
    /// Gets or sets the number of valid resources.
    /// </summary>
    public int ValidCount { get; set; }

    /// <summary>
    /// Gets or sets the number of invalid resources.
    /// </summary>
    public int InvalidCount { get; set; }

    /// <summary>
    /// Gets or sets the number of resources of unknown state.
    /// </summary>
    public int UnknownCount { get; set; }

    /// <summary>
    /// Gets or sets the total size, in bytes, of the resources whose size is declared.
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>
    /// Gets or sets the number of resources whose size is not declared and is not in <see cref="TotalSize"/>.
    /// </summary>
    public int UndeclaredSizeCount { get; set; }
}
