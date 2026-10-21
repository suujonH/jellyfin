using System;
using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MediaBrowser.Common.Net.S3;

namespace Jellyfin.Server.Implementations.NetworkStorage;

/// <summary>
/// Builds addresses of objects served by the network storage Cloudflare Worker
/// (<c>tools/network-storage-worker</c>): <c>{base}/{bucket}/{key}?exp={unix seconds}&amp;sig={signature}</c>, where the
/// signature is the unpadded base64url HMAC-SHA256, keyed with the shared secret, of
/// <c>"v2\n" + bucket + "\n" + key + "\n" + exp</c>. The Worker reads the bucket with its own credentials.
/// </summary>
public static class WorkerUrlSigner
{
    /// <summary>
    /// Gets the address of an object.
    /// </summary>
    /// <param name="baseUrl">The address the Worker is routed at.</param>
    /// <param name="secret">The secret shared with the Worker.</param>
    /// <param name="bucket">The bucket.</param>
    /// <param name="key">The object key, as stored.</param>
    /// <param name="expires">When the address stops being accepted, in Unix seconds.</param>
    /// <returns>The address.</returns>
    public static Uri GetUrl(string baseUrl, string secret, string bucket, string key, long expires)
    {
        ArgumentException.ThrowIfNullOrEmpty(baseUrl);
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentException.ThrowIfNullOrEmpty(bucket);
        ArgumentException.ThrowIfNullOrEmpty(key);

        var exp = expires.ToString(CultureInfo.InvariantCulture);
        return new Uri(
            baseUrl.TrimEnd('/') + "/" + S3UrlSigner.Encode(bucket, false) + "/" + S3UrlSigner.Encode(key, true)
            + "?exp=" + exp + "&sig=" + Sign(secret, bucket, key, exp));
    }

    /// <summary>
    /// Gets the signature of an address.
    /// </summary>
    /// <param name="secret">The secret shared with the Worker.</param>
    /// <param name="bucket">The bucket.</param>
    /// <param name="key">The object key.</param>
    /// <param name="exp">The expiry, in Unix seconds, as written in the address.</param>
    /// <returns>The unpadded base64url signature.</returns>
    public static string Sign(string secret, string bucket, string key, string exp)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("v2\n" + bucket + "\n" + key + "\n" + exp));
        return Base64Url.EncodeToString(mac);
    }
}
