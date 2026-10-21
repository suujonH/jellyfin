using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MediaBrowser.Common.Net.S3;

/// <summary>
/// Builds HTTP addresses of S3 objects, presigned with AWS Signature Version 4 so ffmpeg and clients can read them
/// with plain GET and Range requests and never see the credentials.
/// </summary>
public static class S3UrlSigner
{
    private const string Algorithm = "AWS4-HMAC-SHA256";

    /// <summary>
    /// The longest lifetime S3 accepts for a presigned address.
    /// </summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Gets the HTTP address of an object, presigned for GET.
    /// </summary>
    /// <param name="access">How to reach the service.</param>
    /// <param name="address">The object.</param>
    /// <param name="now">The signing time.</param>
    /// <param name="lifetime">How long the address stays valid; clamped to one second through seven days.</param>
    /// <returns>The address; unsigned when there is no access key.</returns>
    public static Uri GetUrl(S3Access access, S3Address address, DateTime now, TimeSpan lifetime)
    {
        ArgumentNullException.ThrowIfNull(address);
        return GetUrl(access, address.Bucket, address.Key, now, lifetime, "GET", null);
    }

    /// <summary>
    /// Gets a presigned HTTP address of an object or, with an empty key, of the bucket root.
    /// </summary>
    /// <param name="access">How to reach the service.</param>
    /// <param name="bucket">The bucket.</param>
    /// <param name="key">The object key, or empty for the bucket root.</param>
    /// <param name="now">The signing time.</param>
    /// <param name="lifetime">How long the address stays valid; clamped to one second through seven days.</param>
    /// <param name="method">The HTTP method the address is signed for; Signature Version 4 signs it.</param>
    /// <param name="parameters">Further query parameters, signed with the address, such as <c>list-type</c>.</param>
    /// <returns>The address; unsigned when there is no access key.</returns>
    public static Uri GetUrl(
        S3Access access,
        string bucket,
        string key,
        DateTime now,
        TimeSpan lifetime,
        string method,
        IReadOnlyDictionary<string, string>? parameters)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentException.ThrowIfNullOrEmpty(bucket);
        ArgumentNullException.ThrowIfNull(key);
        ArgumentException.ThrowIfNullOrEmpty(method);

        var region = string.IsNullOrEmpty(access.Region) ? "us-east-1" : access.Region;
        var endpoint = new Uri(
            string.IsNullOrEmpty(access.Endpoint) ? "https://s3." + region + ".amazonaws.com" : access.Endpoint,
            UriKind.Absolute);

        var host = access.UsePathStyle ? endpoint.Authority : bucket + "." + endpoint.Authority;
        var path = new StringBuilder(endpoint.AbsolutePath.TrimEnd('/'));
        if (access.UsePathStyle)
        {
            path.Append('/').Append(Encode(bucket, false));
        }

        path.Append('/').Append(Encode(key, true));
        var canonicalUri = path.ToString();
        var origin = endpoint.Scheme + "://" + host;

        // Ordinal order of the names is the canonical order.
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (parameters is not null)
        {
            foreach (var (name, value) in parameters)
            {
                query[name] = value;
            }
        }

        if (string.IsNullOrEmpty(access.AccessKey))
        {
            return new Uri(origin + canonicalUri + (query.Count == 0 ? string.Empty : "?" + JoinQuery(query)));
        }

        var seconds = (long)Math.Clamp(lifetime.TotalSeconds, 1, MaxLifetime.TotalSeconds);
        var utc = now.ToUniversalTime();
        var date = utc.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var timestamp = utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var scope = date + "/" + region + "/s3/aws4_request";

        query["X-Amz-Algorithm"] = Algorithm;
        query["X-Amz-Credential"] = access.AccessKey + "/" + scope;
        query["X-Amz-Date"] = timestamp;
        query["X-Amz-Expires"] = seconds.ToString(CultureInfo.InvariantCulture);
        query["X-Amz-SignedHeaders"] = "host";
        if (!string.IsNullOrEmpty(access.SessionToken))
        {
            query["X-Amz-Security-Token"] = access.SessionToken;
        }

        var canonicalQuery = JoinQuery(query);
        var canonicalRequest = method.ToUpperInvariant() + "\n" + canonicalUri + "\n" + canonicalQuery + "\nhost:" + host + "\n\nhost\nUNSIGNED-PAYLOAD";
        var stringToSign = Algorithm + "\n" + timestamp + "\n" + scope + "\n"
            + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest)));

        var signingKey = Hmac(Encoding.UTF8.GetBytes("AWS4" + access.SecretKey), date);
        signingKey = Hmac(signingKey, region);
        signingKey = Hmac(signingKey, "s3");
        signingKey = Hmac(signingKey, "aws4_request");
        var signature = Convert.ToHexStringLower(Hmac(signingKey, stringToSign));

        return new Uri(origin + canonicalUri + "?" + canonicalQuery + "&X-Amz-Signature=" + signature);
    }

    /// <summary>
    /// Percent-encodes every UTF-8 byte except the RFC 3986 unreserved characters, as Signature Version 4 requires.
    /// </summary>
    /// <param name="value">The value.</param>
    /// <param name="keepSlash">Whether slashes, which separate the parts of a key, stay as they are.</param>
    /// <returns>The encoded value.</returns>
    public static string Encode(string value, bool keepSlash)
    {
        ArgumentNullException.ThrowIfNull(value);

        var result = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~' || (keepSlash && c == '/'))
            {
                result.Append(c);
            }
            else
            {
                result.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
        }

        return result.ToString();
    }

    private static string JoinQuery(SortedDictionary<string, string> query)
    {
        var result = new StringBuilder();
        foreach (var (name, value) in query)
        {
            if (result.Length != 0)
            {
                result.Append('&');
            }

            result.Append(Encode(name, false)).Append('=').Append(Encode(value, false));
        }

        return result.ToString();
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}
