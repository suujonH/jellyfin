using System;

namespace MediaBrowser.Model.NetworkStorage;

/// <summary>
/// The credentials the server reaches one storage with, such as the root of an S3 bucket.
/// Secrets are never returned: <see cref="SecretKey"/> and <see cref="SessionToken"/> are empty in responses, and
/// <see cref="HasSecretKey"/> and <see cref="HasSessionToken"/> tell whether they are set. In a request a <c>null</c>
/// secret keeps the stored one and an empty secret removes it.
/// </summary>
public class NetworkStorageAccessDto
{
    /// <summary>
    /// Gets or sets the id.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the protocol, one of <see cref="NetworkStorageProtocols"/>.
    /// </summary>
    public string Protocol { get; set; } = NetworkStorageProtocols.S3;

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Gets or sets the service address, for example <c>https://s3.us-east-1.amazonaws.com</c> or
    /// <c>https://ACCOUNT_ID.r2.cloudflarestorage.com</c>. Empty means AWS in <see cref="Region"/>.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the bucket. The access is taken to cover the whole bucket.
    /// </summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the signing region; R2 uses <c>auto</c>.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the bucket goes in the path instead of the host name.
    /// </summary>
    public bool UsePathStyle { get; set; }

    /// <summary>
    /// Gets or sets the access key id. Empty means the bucket is read without signing.
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
    /// Gets or sets a value indicating whether a secret key is stored.
    /// </summary>
    public bool HasSecretKey { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether a session token is stored.
    /// </summary>
    public bool HasSessionToken { get; set; }

    /// <summary>
    /// Gets or sets how long, in seconds, an address the server presigns for itself and ffmpeg stays valid.
    /// </summary>
    public int UrlLifetimeSeconds { get; set; } = 43200;

    /// <summary>
    /// Gets or sets a value indicating whether the storage is an archive: it is kept and maintained,
    /// but clients cannot play from it and the server neither streams nor transcodes it for them.
    /// </summary>
    public bool IsArchive { get; set; } = true;

    /// <summary>
    /// Gets or sets settings particular to the protocol, as JSON.
    /// </summary>
    public string? Settings { get; set; }
}
