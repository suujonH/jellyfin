using System;
using System.Diagnostics.CodeAnalysis;

namespace MediaBrowser.Common.Net.S3;

/// <summary>
/// An object named by an <c>s3://bucket/key</c> address.
/// </summary>
/// <param name="Bucket">The bucket.</param>
/// <param name="Key">The object key, as stored: it is not percent-decoded.</param>
public sealed record S3Address(string Bucket, string Key)
{
    /// <summary>
    /// The scheme prefix.
    /// </summary>
    public const string Prefix = "s3://";

    /// <summary>
    /// Gets a value indicating whether a path is written as an S3 address, valid or not.
    /// </summary>
    /// <param name="path">The path.</param>
    /// <returns><c>true</c> when the path starts with <c>s3://</c>.</returns>
    public static bool IsS3Path([NotNullWhen(true)] string? path)
        => path is not null && path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Parses an <c>s3://bucket/key</c> address. The key is taken literally, as the AWS command line does,
    /// so a key with spaces or non-ASCII characters is written as it is.
    /// </summary>
    /// <param name="path">The address.</param>
    /// <param name="address">The parsed address.</param>
    /// <returns><c>true</c> when the path names a bucket and a key.</returns>
    public static bool TryParse([NotNullWhen(true)] string? path, [NotNullWhen(true)] out S3Address? address)
    {
        address = null;
        if (!IsS3Path(path))
        {
            return false;
        }

        var rest = path.AsSpan(Prefix.Length);
        var slash = rest.IndexOf('/');
        if (slash <= 0 || slash == rest.Length - 1)
        {
            return false;
        }

        var bucket = rest[..slash];
        foreach (var c in bucket)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))
            {
                return false;
            }
        }

        address = new S3Address(bucket.ToString(), rest[(slash + 1)..].ToString());
        return true;
    }

    /// <summary>
    /// Gets a value indicating whether a bucket name can be written in an address.
    /// </summary>
    /// <param name="bucket">The bucket name.</param>
    /// <returns><c>true</c> for a name of letters, digits, dots, dashes and underscores.</returns>
    public static bool IsValidBucket([NotNullWhen(true)] string? bucket)
        => !string.IsNullOrEmpty(bucket) && TryParse(Prefix + bucket + "/x", out var address) && address.Bucket == bucket;

    /// <inheritdoc />
    public override string ToString() => Prefix + Bucket + "/" + Key;
}
