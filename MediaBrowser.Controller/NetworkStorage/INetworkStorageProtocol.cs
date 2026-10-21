using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.NetworkStorage;

namespace MediaBrowser.Controller.NetworkStorage;

/// <summary>
/// A protocol <c>.strm</c> files can name resources with. Each one is registered once; the
/// <see cref="INetworkStorageManager"/> asks them in turn which one a target belongs to.
/// </summary>
public interface INetworkStorageProtocol
{
    /// <summary>
    /// Gets the protocol, one of <see cref="NetworkStorageProtocols"/>.
    /// </summary>
    string Protocol { get; }

    /// <summary>
    /// Gets a value indicating whether resources can only be read with an access; without one they are invalid.
    /// </summary>
    bool RequiresAccess { get; }

    /// <summary>
    /// Gets where a target is, when it belongs to this protocol.
    /// </summary>
    /// <param name="target">The target a <c>.strm</c> file names.</param>
    /// <param name="location">Where the target is.</param>
    /// <returns><c>true</c> when the target belongs to this protocol.</returns>
    bool TryLocate(string target, [NotNullWhen(true)] out StorageLocation? location);

    /// <summary>
    /// Gets a value indicating whether an access covers a storage.
    /// </summary>
    /// <param name="access">The access.</param>
    /// <param name="container">The bucket or host.</param>
    /// <returns><c>true</c> when the access is the one to read the storage with.</returns>
    bool Covers(NetworkStorageAccessDto access, string container);

    /// <summary>
    /// Gets the address the server and ffmpeg read a resource from.
    /// </summary>
    /// <param name="access">The access that covers it, if any.</param>
    /// <param name="location">The resource.</param>
    /// <param name="now">The current time.</param>
    /// <param name="method">The HTTP method the address is used with.</param>
    /// <returns>The address, or <c>null</c> when the resource cannot be read without an access.</returns>
    Uri? GetInternalAddress(NetworkStorageAccessDto? access, StorageLocation location, DateTime now, string method);

    /// <summary>
    /// Gets the address a client reads a resource from directly.
    /// </summary>
    /// <param name="access">The access that covers it, if any.</param>
    /// <param name="external">How the storage is published, if it is.</param>
    /// <param name="location">The resource.</param>
    /// <param name="now">The current time.</param>
    /// <returns>The address, or <c>null</c> when clients are not given one and the server streams the resource.</returns>
    Uri? GetExternalAddress(NetworkStorageAccessDto? access, NetworkStorageExternalAddressDto? external, StorageLocation location, DateTime now);

    /// <summary>
    /// Tests that the root of a storage can be read with an access. Whether particular resources exist is not checked.
    /// </summary>
    /// <param name="access">The access.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    Task<NetworkStorageTestResult> TestAsync(NetworkStorageAccessDto access, CancellationToken cancellationToken);

    /// <summary>
    /// Reads the headers of a resource: whether it exists and how large it is. Its content is not read.
    /// </summary>
    /// <param name="access">The access that covers it, if any.</param>
    /// <param name="location">The resource.</param>
    /// <param name="expectedSize">The size the resource should have, if known.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    Task<ObjectCheckResult> CheckAsync(NetworkStorageAccessDto? access, StorageLocation location, long? expectedSize, CancellationToken cancellationToken);
}
