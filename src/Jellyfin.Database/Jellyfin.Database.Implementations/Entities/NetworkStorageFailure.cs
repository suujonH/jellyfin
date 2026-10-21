using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// A network resource that could not be read because it is missing, not permitted, or not the declared size.
/// Busy, failing or unreachable storage is not recorded.
/// </summary>
public class NetworkStorageFailure
{
    /// <summary>
    /// Gets or sets the id of the item whose <c>.strm</c> file names the resource.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the key of the storage, such as <c>s3:bucket</c>.
    /// </summary>
    public required string StorageKey { get; set; }

    /// <summary>
    /// Gets or sets the target as the <c>.strm</c> file names it.
    /// </summary>
    public required string Target { get; set; }

    /// <summary>
    /// Gets or sets what was wrong, a <c>MediaBrowser.Model.NetworkStorage.NetworkStorageIssue</c>.
    /// </summary>
    public int Issue { get; set; }

    /// <summary>
    /// Gets or sets the HTTP status the storage answered with.
    /// </summary>
    public int? StatusCode { get; set; }

    /// <summary>
    /// Gets or sets what found the failure, a <c>MediaBrowser.Model.NetworkStorage.NetworkStorageFailureSource</c>.
    /// </summary>
    public int Source { get; set; }

    /// <summary>
    /// Gets or sets when the failure was first found.
    /// </summary>
    public DateTime DateFirstFailed { get; set; }

    /// <summary>
    /// Gets or sets when the failure was last found.
    /// </summary>
    public DateTime DateLastFailed { get; set; }

    /// <summary>
    /// Gets or sets how often the failure was found.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets the item.
    /// </summary>
    public BaseItemEntity? Item { get; set; }
}
