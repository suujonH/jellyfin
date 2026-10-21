using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Hls;

namespace Jellyfin.Server.Implementations.Tests.Hls;

/// <summary>
/// A local HLS store that clients read directly at an external address, as network storage published at one is read.
/// </summary>
internal sealed class PublicHlsStore : IHlsStore
{
    private readonly IHlsStore _inner;
    private readonly string _externalAddress;
    private readonly TimeSpan _lifetime;

    public PublicHlsStore(IHlsStore inner, string externalAddress, TimeSpan lifetime)
    {
        _inner = inner;
        _externalAddress = externalAddress;
        _lifetime = lifetime;
    }

    public string Location => _inner.Location;

    public bool IsLocal => _inner.IsLocal;

    public Task<string?> CheckRootAsync(IReadOnlyCollection<string> publishedDirectories, CancellationToken cancellationToken)
        => _inner.CheckRootAsync(publishedDirectories, cancellationToken);

    public Task<IReadOnlyList<HlsStoreFile>> ListAsync(string directory, CancellationToken cancellationToken) => _inner.ListAsync(directory, cancellationToken);

    public Task DeleteDirectoryAsync(string directory, CancellationToken cancellationToken) => _inner.DeleteDirectoryAsync(directory, cancellationToken);

    public string? GetServerPath(string path) => _inner.GetServerPath(path);

    public Uri? GetClientAddress(string path) => new(_externalAddress + path + "?sig=1");

    public TimeSpan GetClientAddressLifetime() => _lifetime;

    public Task<bool> HasMarkerAsync(CancellationToken cancellationToken) => _inner.HasMarkerAsync(cancellationToken);
}
