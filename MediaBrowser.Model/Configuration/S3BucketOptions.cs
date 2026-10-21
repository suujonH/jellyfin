namespace MediaBrowser.Model.Configuration;

/// <summary>
/// How to reach one S3 bucket.
/// </summary>
public class S3BucketOptions
{
    /// <summary>
    /// Gets or sets the bucket name. An empty name matches every bucket no other entry names.
    /// </summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the service address, for example <c>https://s3.us-east-1.amazonaws.com</c> or
    /// <c>http://minio.lan:9000</c>. Empty means AWS in <see cref="Region"/>.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the signing region.
    /// </summary>
    public string Region { get; set; } = "us-east-1";

    /// <summary>
    /// Gets or sets a value indicating whether the bucket goes in the path (<c>endpoint/bucket/key</c>) instead of the
    /// host name (<c>bucket.endpoint/key</c>). Most self-hosted services need this.
    /// </summary>
    public bool UsePathStyle { get; set; }

    /// <summary>
    /// Gets or sets the access key. Empty means the bucket is public and addresses are not signed.
    /// </summary>
    public string? AccessKey { get; set; }

    /// <summary>
    /// Gets or sets the secret key.
    /// </summary>
    public string? SecretKey { get; set; }

    /// <summary>
    /// Gets or sets the session token of temporary credentials.
    /// </summary>
    public string? SessionToken { get; set; }
}
