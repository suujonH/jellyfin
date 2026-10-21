#pragma warning disable CA1819 // XML serialization handles collections improperly, so we need to use arrays

namespace MediaBrowser.Model.Configuration;

/// <summary>
/// The S3 buckets <c>.strm</c> files can name with an <c>s3://bucket/key</c> address.
/// </summary>
public class S3Options
{
    /// <summary>
    /// Gets or sets the buckets.
    /// </summary>
    public S3BucketOptions[] Buckets { get; set; } = [];

    /// <summary>
    /// Gets or sets how long, in seconds, a presigned address handed to ffmpeg or a client stays valid.
    /// S3 accepts at most 604800 (seven days).
    /// </summary>
    public int UrlLifetimeSeconds { get; set; } = 43200;
}
