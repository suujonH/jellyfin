using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Hls;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// The HLS store on the machine of a worker, <c>worker-local://{worker id}/{local path}</c>. The server cannot read it: it takes
/// the files the worker reports, deletes nothing there, and clients cannot play from it.
/// </summary>
internal sealed class WorkerLocalHlsStore : IHlsStore
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkerLocalHlsStore"/> class.
    /// </summary>
    /// <param name="location">The root, in its normal form.</param>
    public WorkerLocalHlsStore(string location)
    {
        Location = location;
    }

    /// <inheritdoc />
    public string Location { get; }

    /// <inheritdoc />
    public bool IsLocal => false;

    /// <inheritdoc />
    public Task<string?> CheckRootAsync(IReadOnlyCollection<string> publishedDirectories, CancellationToken cancellationToken)
    {
        // Nothing the server could check: the files are only on the worker, which checks the root itself.
        return Task.FromResult<string?>(null);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HlsStoreFile>> ListAsync(string directory, CancellationToken cancellationToken)
        => throw new IOException("The HLS store is on a worker: " + Location);

    /// <inheritdoc />
    public Task DeleteDirectoryAsync(string directory, CancellationToken cancellationToken)
    {
        // The files are on the worker, which cleans them up itself.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public string? GetServerPath(string path) => null;

    /// <inheritdoc />
    public Uri? GetClientAddress(string path) => null;

    /// <inheritdoc />
    public TimeSpan GetClientAddressLifetime() => TimeSpan.Zero;

    /// <inheritdoc />
    public Task<bool> HasMarkerAsync(CancellationToken cancellationToken) => Task.FromResult(true);
}
