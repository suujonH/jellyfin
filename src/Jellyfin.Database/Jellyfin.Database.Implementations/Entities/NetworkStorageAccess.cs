using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// The credentials the server reaches one storage with, such as the root of an S3 bucket.
/// </summary>
public class NetworkStorageAccess
{
    /// <summary>
    /// Gets or sets the id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the protocol, such as <c>s3</c>.
    /// </summary>
    public required string Protocol { get; set; }

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the service address; empty means the default of the protocol.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the bucket; the access covers all of it.
    /// </summary>
    public required string Bucket { get; set; }

    /// <summary>
    /// Gets or sets the signing region.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the bucket goes in the path instead of the host name.
    /// </summary>
    public bool UsePathStyle { get; set; }

    /// <summary>
    /// Gets or sets the access key id.
    /// </summary>
    public string? AccessKey { get; set; }

    /// <summary>
    /// Gets or sets the secret access key.
    /// </summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// Gets or sets the session token of temporary credentials.
    /// </summary>
    public string? SessionToken { get; set; }

    /// <summary>
    /// Gets or sets how long, in seconds, an address the server presigns for itself stays valid.
    /// </summary>
    public int UrlLifetimeSeconds { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the storage is an archive: it is kept and maintained,
    /// but clients cannot play from it and the server neither streams nor transcodes it for them.
    /// </summary>
    public bool IsArchive { get; set; } = true;

    /// <summary>
    /// Gets or sets settings particular to the protocol, as JSON.
    /// </summary>
    public string? Settings { get; set; }

    /// <summary>
    /// Gets or sets when the access was added.
    /// </summary>
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// Gets or sets when the access was last changed.
    /// </summary>
    public DateTime DateModified { get; set; }

    /// <summary>
    /// Gets or sets how the storage is published to clients.
    /// </summary>
    public NetworkStorageExternalAddress? ExternalAddress { get; set; }
}
