using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// Whether and how clients read the resources of a storage directly instead of through the server.
/// </summary>
public class NetworkStorageExternalAddress
{
    /// <summary>
    /// Gets or sets the id of the access the storage is reached with.
    /// </summary>
    public Guid AccessId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether clients are given addresses of the storage: whether <see cref="AuthMode"/> is set.
    /// A row written while the two were set apart may have an authentication method with this <c>false</c>; it is private.
    /// </summary>
    public bool IsPublic { get; set; }

    /// <summary>
    /// Gets or sets how clients are given access, a <c>MediaBrowser.Model.NetworkStorage.ExternalAuthMode</c>.
    /// </summary>
    public int? AuthMode { get; set; }

    /// <summary>
    /// Gets or sets the address clients reach the storage at.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the secret shared with the Cloudflare Worker.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Gets or sets how long, in seconds, an address given to a client stays valid.
    /// </summary>
    public int UrlLifetimeSeconds { get; set; }

    /// <summary>
    /// Gets or sets the access.
    /// </summary>
    public NetworkStorageAccess? Access { get; set; }
}
