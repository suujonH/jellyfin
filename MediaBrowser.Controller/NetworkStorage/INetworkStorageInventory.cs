using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.NetworkStorage;
using MediaBrowser.Model.Querying;

namespace MediaBrowser.Controller.NetworkStorage;

/// <summary>
/// What the library has in each network storage: the sources of its items and their pre-encoded HLS, from what the library
/// already knows.
/// </summary>
public interface INetworkStorageInventory
{
    /// <summary>
    /// Gets statistics of every storage the library has resources in, and of every access.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The storages.</returns>
    Task<IReadOnlyList<NetworkStorageSummary>> GetStoragesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the groups of the resources of one storage: its libraries with their series, and the contents no item refers to.
    /// </summary>
    /// <param name="storageKey">The key of the storage.</param>
    /// <param name="states">The states to include; empty includes all.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The groups that have resources of those states, the orphans last.</returns>
    Task<IReadOnlyList<NetworkStorageGroup>> GetGroupsAsync(string storageKey, IReadOnlyCollection<NetworkStorageResourceState> states, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the resources of one storage, or of one group of it.
    /// </summary>
    /// <param name="storageKey">The key of the storage.</param>
    /// <param name="states">The states to include; empty includes all.</param>
    /// <param name="group">The key of a group, as <see cref="NetworkStorageGroup.Key"/>; <c>null</c> for the whole storage.</param>
    /// <param name="startIndex">The index of the first resource.</param>
    /// <param name="limit">The most resources to return, or <c>null</c> for all.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resources: of a group in episode order, of the whole storage invalid first; none for an unknown group.</returns>
    Task<QueryResult<NetworkStorageResource>> GetResourcesAsync(
        string storageKey,
        IReadOnlyCollection<NetworkStorageResourceState> states,
        string? group,
        int startIndex,
        int? limit,
        CancellationToken cancellationToken);
}
