using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.NetworkStorage;

namespace MediaBrowser.Controller.NetworkStorage;

/// <summary>
/// A protocol whose storage the server can also list and delete in, as the HLS store does. Http cannot be.
/// </summary>
public interface IObjectStorageProtocol : INetworkStorageProtocol
{
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
