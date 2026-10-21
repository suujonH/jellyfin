namespace MediaBrowser.Model.NetworkStorage;

/// <summary>
/// The protocols network storage is reached with.
/// </summary>
public static class NetworkStorageProtocols
{
    /// <summary>
    /// S3 and S3-compatible services; <c>.strm</c> files name objects as <c>s3://bucket/key</c>.
    /// </summary>
    public const string S3 = "s3";

    /// <summary>
    /// Plain http and https addresses named by <c>.strm</c> files.
    /// </summary>
    public const string Http = "http";
}
