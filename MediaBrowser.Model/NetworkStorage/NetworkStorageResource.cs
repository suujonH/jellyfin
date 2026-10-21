using System;

namespace MediaBrowser.Model.NetworkStorage;

/// <summary>
/// A resource of network storage: a library item whose <c>.strm</c> file names it.
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
    /// Gets or sets the declared size, in bytes.
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
}
