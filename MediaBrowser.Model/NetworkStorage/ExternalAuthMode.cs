namespace MediaBrowser.Model.NetworkStorage;

/// <summary>
/// How clients are given access to a storage that is public.
/// </summary>
public enum ExternalAuthMode
{
    /// <summary>
    /// Through a Cloudflare Worker that checks an expiring signature and reads the bucket with its own credentials.
    /// </summary>
    CloudflareWorker = 0,

    /// <summary>
    /// The bucket, or a domain in front of it, can be read by anyone.
    /// </summary>
    PublicBucket = 1,

    /// <summary>
    /// Through S3 addresses presigned with the credentials of the access.
    /// </summary>
    PresignedUrl = 2
}
