using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net.S3;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.NetworkStorage;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// The HLS store under a prefix of a bucket that an access of network storage covers. Workers write it with their own
/// credentials, and clients read it directly when the storage is published at an external address.
/// </summary>
internal sealed class NetworkHlsStore : IHlsStore
{
    private readonly INetworkStorageManager _networkStorage;
    private readonly string _bucket;
    private readonly string _prefix;

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkHlsStore"/> class.
    /// </summary>
    /// <param name="networkStorage">The network storage.</param>
    /// <param name="bucket">The bucket.</param>
    /// <param name="prefix">The prefix of the keys: empty, or a valid path that ends with a slash.</param>
    public NetworkHlsStore(INetworkStorageManager networkStorage, string bucket, string prefix)
    {
        _networkStorage = networkStorage;
        _bucket = bucket;
        _prefix = prefix;
    }

    /// <inheritdoc />
    public string Location => S3Address.Prefix + _bucket + (_prefix.Length == 0 ? string.Empty : "/" + _prefix.TrimEnd('/'));

    /// <inheritdoc />
    public bool IsLocal => false;

    /// <inheritdoc />
    public async Task<string?> CheckRootAsync(IReadOnlyCollection<string> publishedDirectories, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publishedDirectories);
        var prefixes = publishedDirectories.Select(directory => GetTarget(directory) + "/").ToArray();
        if (GetAccessProblem() is { } problem)
        {
            return problem;
        }

        try
        {
            if (await HasMarkerAsync(cancellationToken).ConfigureAwait(false) || prefixes.Length == 0)
            {
                return null;
            }

            foreach (var prefix in prefixes)
            {
                if ((await _networkStorage.ListAsync(prefix, 1, cancellationToken).ConfigureAwait(false)).Count > 0)
                {
                    return null;
                }
            }

            // Neither marked nor holding a published directory: the access may have been changed to another bucket or
            // endpoint, where everything would seem to be lost.
            return "The HLS store has no marker " + HlsStorePaths.MarkerName + " and none of the published renditions; its access may name other storage: " + Location;
        }
        catch (IOException ex)
        {
            return "The HLS store cannot be used: " + ex.Message;
        }
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HlsStoreFile>> ListAsync(string directory, CancellationToken cancellationToken)
    {
        var prefix = _prefix + GetKey(directory) + "/";
        var objects = await _networkStorage.ListAsync(S3Address.Prefix + _bucket + "/" + prefix, null, cancellationToken).ConfigureAwait(false);

        // A key that is the prefix itself is a folder some tools create, not a file.
        return objects
            .Where(o => o.Key.Length > prefix.Length && o.Key.StartsWith(prefix, StringComparison.Ordinal))
            .Select(o => new HlsStoreFile(o.Key[prefix.Length..], o.Size, o.LastModified))
            .ToArray();
    }

    /// <inheritdoc />
    public async Task DeleteDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        var prefix = GetTarget(directory) + "/";
        var objects = await _networkStorage.ListAsync(prefix, null, cancellationToken).ConfigureAwait(false);
        if (objects.Count > 0)
        {
            await _networkStorage.DeleteAsync(objects.Select(o => S3Address.Prefix + _bucket + "/" + o.Key).ToArray(), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public string? GetServerPath(string path)
    {
        // An archive is read for maintenance only; it is not played, as the files of a library in one are not.
        var target = GetTarget(path);
        return _networkStorage.IsArchived(target) ? null : _networkStorage.ResolveShortcutTarget(target);
    }

    /// <inheritdoc />
    public Uri? GetClientAddress(string path) => _networkStorage.GetClientAddress(GetTarget(path));

    /// <inheritdoc />
    public TimeSpan GetClientAddressLifetime() => _networkStorage.GetClientAddressLifetime(GetTarget(HlsStorePaths.MarkerName));

    /// <inheritdoc />
    public async Task<bool> HasMarkerAsync(CancellationToken cancellationToken)
    {
        // A key sorts before the longer keys it starts, so the first one listed is the marker if any.
        foreach (var name in new[] { HlsStorePaths.MarkerName, HlsStorePaths.LegacyMarkerName })
        {
            var key = _prefix + name;
            var objects = await _networkStorage.ListAsync(S3Address.Prefix + _bucket + "/" + key, 1, cancellationToken).ConfigureAwait(false);
            if (objects.Count > 0 && string.Equals(objects[0].Key, key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    // Why the store cannot be used with its access, or null.
    private string? GetAccessProblem()
    {
        var marker = GetTarget(HlsStorePaths.MarkerName);
        if (_networkStorage.GetServerAddress(marker, "GET") is null)
        {
            return "No access covers bucket " + _bucket + " of the HLS store.";
        }

        // Clients cannot play from an archive.
        return _networkStorage.IsArchived(marker) ? "The access of bucket " + _bucket + " of the HLS store is an archive." : null;
    }

    private string GetTarget(string path) => S3Address.Prefix + _bucket + "/" + _prefix + GetKey(path);

    private static string GetKey(string path)
    {
        HlsStorePaths.Validate(path, nameof(path));
        return path;
    }
}
