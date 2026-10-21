using System;

namespace MediaBrowser.Model.NetworkStorage;

/// <summary>
/// Whether and how clients read the resources of a storage directly instead of through the server.
/// <see cref="Secret"/> is never returned; <see cref="HasSecret"/> tells whether one is set, and in a request
/// a <c>null</c> secret keeps the stored one.
/// </summary>
public class NetworkStorageExternalAddressDto
{
    /// <summary>
    /// Gets or sets the id of the access the storage is reached with.
    /// </summary>
    public Guid AccessId { get; set; }

    /// <summary>
    /// Gets a value indicating whether clients are given addresses of the storage: whether an external address is set.
    /// When <c>false</c>, clients get no address of it and the server streams its resources.
    /// </summary>
    public bool IsPublic => AuthMode.HasValue;

    /// <summary>
    /// Gets or sets how clients are given access; <c>null</c> when no external address is set, which keeps the storage private.
    /// </summary>
    public ExternalAuthMode? AuthMode { get; set; }

    /// <summary>
    /// Gets or sets the address clients reach the storage at: the Worker route, the public domain of the bucket,
    /// or, for presigned addresses, an endpoint other than the one the server uses.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// Gets or sets the secret shared with the Cloudflare Worker.
    /// </summary>
    public string? Secret { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a secret is stored.
    /// </summary>
    public bool HasSecret { get; set; }

    /// <summary>
    /// Gets or sets how long, in seconds, an address given to a client stays valid.
    /// </summary>
    public int UrlLifetimeSeconds { get; set; } = 21600;
}
