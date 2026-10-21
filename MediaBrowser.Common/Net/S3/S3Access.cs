namespace MediaBrowser.Common.Net.S3;

/// <summary>
/// How to reach and sign requests to an S3 service.
/// </summary>
/// <param name="Endpoint">The service address, for example <c>https://s3.us-east-1.amazonaws.com</c> or
/// <c>http://minio.lan:9000</c>. Empty means AWS in <paramref name="Region"/>, under <c>amazonaws.com.cn</c> for a <c>cn-</c> region.</param>
/// <param name="Region">The signing region; empty means <c>us-east-1</c>.</param>
/// <param name="UsePathStyle">Whether the bucket goes in the path (<c>endpoint/bucket/key</c>) instead of the
/// host name (<c>bucket.endpoint/key</c>). Most self-hosted services need this.</param>
/// <param name="AccessKey">The access key. Empty means the bucket is public and addresses are not signed.</param>
/// <param name="SecretKey">The secret key.</param>
/// <param name="SessionToken">The session token of temporary credentials.</param>
public sealed record S3Access(
    string? Endpoint,
    string? Region,
    bool UsePathStyle,
    string? AccessKey,
    string? SecretKey,
    string? SessionToken);
