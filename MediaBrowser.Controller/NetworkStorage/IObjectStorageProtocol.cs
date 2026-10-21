using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.NetworkStorage;

namespace MediaBrowser.Controller.NetworkStorage;

/// <summary>
/// A protocol whose storage the server can also list, write and delete in, as the HLS store does. Http cannot be written.
/// </summary>
public interface IObjectStorageProtocol : INetworkStorageProtocol
{
    /// <summary>
    /// Gets an address an object can be written at with PUT, at the endpoint of the access.
    /// </summary>
    /// <param name="access">The access that covers it.</param>
    /// <param name="location">The object.</param>
    /// <param name="now">The current time.</param>
    /// <param name="lifetime">How long the address stays valid.</param>
    /// <returns>The address.</returns>
    Uri GetUploadAddress(NetworkStorageAccessDto access, StorageLocation location, DateTime now, TimeSpan lifetime);

    /// <summary>
    /// Lists the objects whose keys start with a prefix, in key order.
    /// </summary>
    /// <param name="access">The access that covers them.</param>
    /// <param name="prefix">The bucket and the prefix of the keys.</param>
    /// <param name="limit">The most objects to list, or <c>null</c> for all.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The objects.</returns>
    /// <exception cref="IOException">The storage refused the listing or could not be reached.</exception>
    Task<IReadOnlyList<StorageObject>> ListAsync(NetworkStorageAccessDto access, StorageLocation prefix, int? limit, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes objects; one that does not exist is deleted already.
    /// </summary>
    /// <param name="access">The access that covers them.</param>
    /// <param name="container">The bucket.</param>
    /// <param name="keys">The keys of the objects.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="IOException">The storage refused to delete an object or could not be reached.</exception>
    Task DeleteAsync(NetworkStorageAccessDto access, string container, IReadOnlyCollection<string> keys, CancellationToken cancellationToken);
}
