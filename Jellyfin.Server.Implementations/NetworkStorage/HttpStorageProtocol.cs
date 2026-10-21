using System;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Model.NetworkStorage;

namespace Jellyfin.Server.Implementations.NetworkStorage;

/// <summary>
/// Plain http and https addresses named by <c>.strm</c> files. They are read as written, without an access,
/// and are grouped by host.
/// </summary>
public class HttpStorageProtocol : INetworkStorageProtocol
{
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="HttpStorageProtocol"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public HttpStorageProtocol(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public string Protocol => NetworkStorageProtocols.Http;

    /// <inheritdoc />
    public bool RequiresAccess => false;

    /// <inheritdoc />
    public bool TryLocate(string target, [NotNullWhen(true)] out StorageLocation? location)
    {
        location = null;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        location = new StorageLocation(Protocol, uri.Authority, uri.PathAndQuery, target);
        return true;
    }

    /// <inheritdoc />
    public bool Covers(NetworkStorageAccessDto access, string container) => false;

    /// <inheritdoc />
    public Uri? GetInternalAddress(NetworkStorageAccessDto? access, StorageLocation location, DateTime now, string method)
    {
        ArgumentNullException.ThrowIfNull(location);
        return new Uri(location.Target);
    }

    /// <inheritdoc />
    public Uri? GetExternalAddress(NetworkStorageAccessDto? access, NetworkStorageExternalAddressDto? external, StorageLocation location, DateTime now)
    {
        // The address is written in the .strm file and carries nothing of the server's.
        ArgumentNullException.ThrowIfNull(location);
        return new Uri(location.Target);
    }

    /// <inheritdoc />
    public TimeSpan GetExternalAddressLifetime(NetworkStorageExternalAddressDto? external) => TimeSpan.Zero;

    /// <inheritdoc />
    public Task<NetworkStorageTestResult> TestAsync(NetworkStorageAccessDto access, CancellationToken cancellationToken)
        => Task.FromResult(new NetworkStorageTestResult(false, null, "Http addresses need no access."));

    /// <inheritdoc />
    public Task<ObjectCheckResult> CheckAsync(NetworkStorageAccessDto? access, StorageLocation location, long? expectedSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        var uri = new Uri(location.Target);
        return NetworkStorageHttp.CheckAsync(_httpClientFactory.CreateClient(NamedClient.Default), uri, uri, expectedSize, cancellationToken);
    }
}
