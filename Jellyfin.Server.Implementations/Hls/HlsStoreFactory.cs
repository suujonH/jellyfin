using System;
using System.IO;
using MediaBrowser.Common.Net.S3;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Model.Hls;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// Opens the HLS store a root names: a local directory, a prefix of a bucket of network storage, or a directory on the machine of a
/// worker, <c>worker-local://{worker id}/{local path}</c>.
/// </summary>
public class HlsStoreFactory : IHlsStoreFactory
{
    private readonly INetworkStorageManager _networkStorage;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsStoreFactory"/> class.
    /// </summary>
    /// <param name="networkStorage">The network storage.</param>
    public HlsStoreFactory(INetworkStorageManager networkStorage)
    {
        _networkStorage = networkStorage;
    }

    /// <inheritdoc />
    public IHlsStore Create(string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        if (HlsWorkerLocalAddress.TryParse(location.Trim(), out var worker, out var localPath))
        {
            var trimmed = localPath.TrimEnd('/', '\\');
            return new WorkerLocalHlsStore(HlsWorkerLocalAddress.Format(worker, trimmed.Length == 0 ? localPath : trimmed));
        }

        var (bucket, path) = Parse(location);
        return bucket is null
            ? new LocalHlsStore(path)
            : new NetworkHlsStore(_networkStorage, bucket, path.Length == 0 ? string.Empty : path + "/");
    }

    // The bucket and the prefix of network storage, or no bucket and the full path of a local directory.
    private static (string? Bucket, string Path) Parse(string location)
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

            return (bucket, prefix);
        }

        if (!Path.IsPathFullyQualified(location))
        {
            throw new ArgumentException("The HLS store must be an absolute local path or s3://bucket/prefix: " + location, nameof(location));
        }

        return (null, Path.TrimEndingDirectorySeparator(Path.GetFullPath(location)));
    }
}
