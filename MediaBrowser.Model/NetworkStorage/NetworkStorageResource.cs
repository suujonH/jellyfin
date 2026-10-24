using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.NetworkStorage;

/// <summary>
/// A resource of network storage: a library item whose source or pre-encoded HLS is in it, or a content no item refers to any
/// longer.
/// </summary>
public class NetworkStorageResource
{
    /// <summary>
    /// Gets or sets the item id.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the item name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the series of an episode.
    /// </summary>
    public string? SeriesName { get; set; }

    /// <summary>
    /// Gets or sets the item type.
    /// </summary>
    public string? Type { get; set; }

    /// <summary>
    /// Gets or sets the path of the <c>.strm</c> file.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>
    /// Gets or sets the address the <c>.strm</c> file names, as written in it.
    /// </summary>
    public string? Target { get; set; }

    /// <summary>
    /// Gets or sets the size of the source media, in bytes, when known.
    /// </summary>
    public long? Size { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the resource has a valid <c>__true_cache__</c> entry.
    /// </summary>
    public bool HasTrueCache { get; set; }

    /// <summary>
    /// Gets or sets the state.
    /// </summary>
    public NetworkStorageResourceState State { get; set; }

    /// <summary>
    /// Gets or sets why the resource is invalid or unknown.
    /// </summary>
    public NetworkStorageIssue? Issue { get; set; }

    /// <summary>
    /// Gets or sets the HTTP status a failed read was answered with.
    /// </summary>
    public int? StatusCode { get; set; }

    /// <summary>
    /// Gets or sets what found the failure.
    /// </summary>
    public NetworkStorageFailureSource? FailureSource { get; set; }

    /// <summary>
    /// Gets or sets when reading the resource last failed.
    /// </summary>
    public DateTime? DateLastFailed { get; set; }

    /// <summary>
    /// Gets or sets how often reading the resource failed.
    /// </summary>
    public int FailureCount { get; set; }

    /// <summary>
    /// Gets or sets the library of the item.
    /// </summary>
    public Guid? LibraryId { get; set; }

    /// <summary>
    /// Gets or sets the name of the library of the item.
    /// </summary>
    public string? LibraryName { get; set; }

    /// <summary>
    /// Gets or sets the series of the item.
    /// </summary>
    public Guid? SeriesId { get; set; }

    /// <summary>
    /// Gets or sets the season number of the item.
    /// </summary>
    public int? SeasonNumber { get; set; }

    /// <summary>
    /// Gets or sets the episode number of the item.
    /// </summary>
    public int? EpisodeNumber { get; set; }

    /// <summary>
    /// Gets or sets the hash of the pre-encoded content of the item.
    /// </summary>
    public string? Sha256 { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the source is in the storage listed.
    /// </summary>
    public bool SourceInStorage { get; set; }

    /// <summary>
    /// Gets or sets the key of the storage the source is in; <c>null</c> when it is in no network storage.
    /// </summary>
    public string? SourceStorageKey { get; set; }

    /// <summary>
    /// Gets or sets the size, in bytes, of the pre-encoded HLS in the storage listed.
    /// </summary>
    public long HlsSize { get; set; }

    /// <summary>
    /// Gets or sets the size, in bytes, of what of the resource is in the storage listed: its source and its HLS.
    /// </summary>
    public long TotalSize { get; set; }

    /// <summary>
    /// Gets or sets the published renditions of the content, wherever they are.
    /// </summary>
    public IReadOnlyList<NetworkStorageHlsRendition> HlsRenditions { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether no item refers to the content any longer.
    /// </summary>
    public bool IsOrphan { get; set; }

    /// <summary>
    /// Gets or sets when a content no item refers to is deleted.
    /// </summary>
    public DateTime? DateOrphanExpires { get; set; }
}
