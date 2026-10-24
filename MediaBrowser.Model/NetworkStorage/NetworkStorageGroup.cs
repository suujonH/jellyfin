using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.NetworkStorage;

/// <summary>
/// A group of the resources of a storage, with their statistics.
/// </summary>
public class NetworkStorageGroup
{
    /// <summary>
    /// Gets or sets the key, which lists the resources of the group: <c>library:{id}</c>, <c>library:none</c>,
    /// <c>series:{id}</c> or <c>orphans</c>.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the kind.
    /// </summary>
    public NetworkStorageGroupKind Kind { get; set; }

    /// <summary>
    /// Gets or sets the name of the library or the series; <c>null</c> when it is not known, and for the orphans.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the library.
    /// </summary>
    public Guid? LibraryId { get; set; }

    /// <summary>
    /// Gets or sets the series.
    /// </summary>
    public Guid? SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the number of resources.
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
    /// Gets or sets the number of resources in an unknown state.
    /// </summary>
    public int UnknownCount { get; set; }

    /// <summary>
    /// Gets or sets the number of resources whose source is in the storage without a declared size.
    /// </summary>
    public int UndeclaredSizeCount { get; set; }

    /// <summary>
    /// Gets or sets the size, in bytes, of what of the group is in the storage.
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>
    /// Gets or sets the size, in bytes, of the pre-encoded HLS of the group in the storage, each content once.
    /// </summary>
    public long HlsSize { get; set; }

    /// <summary>
    /// Gets or sets the size, in bytes, of the sources of the group in the storage.
    /// </summary>
    public long SourceSize { get; set; }

    /// <summary>
    /// Gets or sets the number of resources of a library that are in no series.
    /// </summary>
    public int LooseCount { get; set; }

    /// <summary>
    /// Gets or sets the series of a library.
    /// </summary>
    public IReadOnlyList<NetworkStorageGroup> Children { get; set; } = [];
}
