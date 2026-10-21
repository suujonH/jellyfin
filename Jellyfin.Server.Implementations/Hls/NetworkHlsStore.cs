using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.NetworkStorage;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Net.S3;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Model.Hls;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// The HLS store under a prefix of a bucket that an access of network storage covers. Workers upload to it directly, and
/// clients read it directly when the storage is published at an external address.
/// </summary>
internal sealed class NetworkHlsStore : IHlsStore
{
    // The lifetime of the addresses the server writes and reads the root with itself, right away.
    private static readonly TimeSpan _requestLifetime = TimeSpan.FromMinutes(15);

    private readonly INetworkStorageManager _networkStorage;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string _bucket;
    private readonly string _prefix;

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkHlsStore"/> class.
    /// </summary>
    /// <param name="networkStorage">The network storage.</param>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    /// <param name="bucket">The bucket.</param>
    /// <param name="prefix">The prefix of the keys: empty, or a valid path that ends with a slash.</param>
    public NetworkHlsStore(INetworkStorageManager networkStorage, IHttpClientFactory httpClientFactory, string bucket, string prefix)
    {
        _networkStorage = networkStorage;
        _httpClientFactory = httpClientFactory;
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
                    // The store itself, checked the first time since something was published in it, or a store that predates
                    // the marker.
                    await PutAsync(HlsStorePaths.MarkerName, Encoding.UTF8.GetBytes(HlsStorePaths.MarkerContent), cancellationToken).ConfigureAwait(false);
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
    public Task WriteAsync(string path, Stream content, CancellationToken cancellationToken)
        => throw new NotSupportedException("Workers upload to network storage directly.");

    /// <inheritdoc />
    public Uri? GetUploadAddress(string path, TimeSpan lifetime) => _networkStorage.GetUploadAddress(GetTarget(path), lifetime);

    /// <inheritdoc />
    public string? GetServerPath(string path) => _networkStorage.ResolveShortcutTarget(GetTarget(path));

    /// <inheritdoc />
    public Uri? GetClientAddress(string path) => _networkStorage.GetClientAddress(GetTarget(path));

    /// <inheritdoc />
    public async Task<HlsStoreTestResult> TestAsync(CancellationToken cancellationToken)
    {
        if (GetAccessProblem() is { } problem)
        {
            return new HlsStoreTestResult(false, false, problem);
        }

        var hasMarker = false;
        var path = HlsStorePaths.TestNamePrefix + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var isWritten = false;
        try
        {
            hasMarker = await HasMarkerAsync(cancellationToken).ConfigureAwait(false);
            var content = Guid.NewGuid().ToByteArray();
            await PutAsync(path, content, cancellationToken).ConfigureAwait(false);
            isWritten = true;
            var read = await GetAsync(path, cancellationToken).ConfigureAwait(false);
            await _networkStorage.DeleteAsync([GetTarget(path)], cancellationToken).ConfigureAwait(false);
            isWritten = false;
            return read.AsSpan().SequenceEqual(content)
                ? new HlsStoreTestResult(true, hasMarker, null)
                : new HlsStoreTestResult(false, hasMarker, "The test object read back is not what was written.");
        }
        catch (IOException ex)
        {
            return new HlsStoreTestResult(false, hasMarker, ex.Message);
        }
        finally
        {
            if (isWritten)
            {
                await TryDeleteAsync(path).ConfigureAwait(false);
            }
        }
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

    private async Task<bool> HasMarkerAsync(CancellationToken cancellationToken)
    {
        // A key sorts before the longer keys it starts, such as those of tests, so the first one listed is the marker if any.
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

    private async Task PutAsync(string path, byte[] content, CancellationToken cancellationToken)
    {
        var target = GetTarget(path);
        var url = _networkStorage.GetUploadAddress(target, _requestLifetime) ?? throw new IOException("No access covers bucket " + _bucket + ".");
        using var request = new HttpRequestMessage(HttpMethod.Put, url) { Content = new ByteArrayContent(content) };
        await SendAsync(request, "Writing " + target, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> GetAsync(string path, CancellationToken cancellationToken)
    {
        var target = GetTarget(path);
        var url = _networkStorage.GetServerAddress(target, "GET") ?? throw new IOException("No access covers bucket " + _bucket + ".");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await SendAsync(request, "Reading " + target, cancellationToken).ConfigureAwait(false);
    }

    private async Task<byte[]> SendAsync(HttpRequestMessage request, string what, CancellationToken cancellationToken)
    {
        var (status, body) = await NetworkStorageHttp.SendAsync(_httpClientFactory.CreateClient(NamedClient.Default), request, what, cancellationToken)
            .ConfigureAwait(false);
        return NetworkStorageHttp.IsSuccess(status) ? body : throw S3StorageProtocol.CreateError(what, status, body);
    }

    private async Task TryDeleteAsync(string path)
    {
        try
        {
            await _networkStorage.DeleteAsync([GetTarget(path)], CancellationToken.None).ConfigureAwait(false);
        }
        catch (IOException)
        {
            // Left behind; the name tells what it is.
        }
    }

    private string GetTarget(string path) => S3Address.Prefix + _bucket + "/" + _prefix + GetKey(path);

    private static string GetKey(string path)
    {
        HlsStorePaths.Validate(path, nameof(path));
        return path;
    }
}
