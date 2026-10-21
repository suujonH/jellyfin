using System;
using System.IO;
using System.Net.Http;
using MediaBrowser.Common.Net.S3;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.NetworkStorage;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// Opens the HLS store a setting names: a local directory, or a prefix of a bucket of network storage.
/// </summary>
public class HlsStoreFactory : IHlsStoreFactory
{
    private readonly INetworkStorageManager _networkStorage;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsStoreFactory"/> class.
    /// </summary>
    /// <param name="networkStorage">The network storage.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public HlsStoreFactory(INetworkStorageManager networkStorage, IHttpClientFactory httpClientFactory)
    {
        _networkStorage = networkStorage;
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public IHlsStore Create(string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        location = location.Trim();
        if (S3Address.IsS3Path(location))
        {
            // Unlike the target of a .strm file, the key may be empty: the store is then the whole bucket.
            var rest = location[S3Address.Prefix.Length..];
            var slash = rest.IndexOf('/', StringComparison.Ordinal);
            var bucket = slash < 0 ? rest : rest[..slash];
            var prefix = slash < 0 ? string.Empty : rest[(slash + 1)..].TrimEnd('/');
            if (!S3Address.IsValidBucket(bucket))
            {
                throw new ArgumentException("The bucket name of the HLS store is missing or invalid: " + location, nameof(location));
            }

            if (prefix.Length > 0)
            {
                HlsStorePaths.Validate(prefix, nameof(location));
            }

            return new NetworkHlsStore(_networkStorage, _httpClientFactory, bucket, prefix.Length == 0 ? string.Empty : prefix + "/");
        }

        if (!Path.IsPathFullyQualified(location))
        {
            throw new ArgumentException("The HLS store must be an absolute local path or s3://bucket/prefix: " + location, nameof(location));
        }

        return new LocalHlsStore(Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)));
    }
}
