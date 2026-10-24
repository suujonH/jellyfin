using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.NetworkStorage;
using MediaBrowser.Model.Querying;

namespace MediaBrowser.Controller.NetworkStorage;

/// <summary>
/// The storages <c>.strm</c> files name resources in: how the server reaches them, whether clients may reach them
/// directly, and which resources can be read.
/// </summary>
public interface INetworkStorageManager
{
    /// <summary>
    /// Gets the accesses, with their secrets.
    /// </summary>
    /// <returns>The accesses.</returns>
    IReadOnlyList<NetworkStorageAccessDto> GetAccesses();

    /// <summary>
    /// Gets an access, with its secrets.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <returns>The access, or <c>null</c>.</returns>
    NetworkStorageAccessDto? GetAccess(Guid id);

    /// <summary>
    /// Adds an access, or changes the one with the same id. A <c>null</c> secret keeps the stored one.
    /// </summary>
    /// <param name="access">The access.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stored access.</returns>
    /// <exception cref="ArgumentException">The access is incomplete or another one covers the same storage.</exception>
    /// <exception cref="MediaBrowser.Common.Extensions.ResourceNotFoundException">The id names no access.</exception>
    Task<NetworkStorageAccessDto> SaveAccessAsync(NetworkStorageAccessDto access, CancellationToken cancellationToken);

    /// <summary>
    /// Removes an access and how its storage is published.
    /// </summary>
    /// <param name="id">The id.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>true</c> when there was such an access.</returns>
    Task<bool> DeleteAccessAsync(Guid id, CancellationToken cancellationToken);

    /// <summary>
    /// Tests that the root of a storage can be read with an access, saved or not. A <c>null</c> secret
    /// of a saved access is taken from the stored one.
    /// </summary>
    /// <param name="access">The access.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    Task<NetworkStorageTestResult> TestAccessAsync(NetworkStorageAccessDto access, CancellationToken cancellationToken);

    /// <summary>
    /// Gets how the storage of an access is published, with its secret.
    /// </summary>
    /// <param name="accessId">The id of the access.</param>
    /// <returns>The settings; a storage that was never published is not public.</returns>
    NetworkStorageExternalAddressDto GetExternalAddress(Guid accessId);

    /// <summary>
    /// Sets how the storage of an access is published. A <c>null</c> secret keeps the stored one.
    /// </summary>
    /// <param name="external">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The stored settings.</returns>
    /// <exception cref="ArgumentException">A public storage lacks what its authentication needs.</exception>
    /// <exception cref="MediaBrowser.Common.Extensions.ResourceNotFoundException">The id names no access.</exception>
    Task<NetworkStorageExternalAddressDto> SaveExternalAddressAsync(NetworkStorageExternalAddressDto external, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the address the server and ffmpeg read the target of a <c>.strm</c> file from, to stream, transcode or
    /// probe it: the external address when the storage is published at one, otherwise the endpoint of its access.
    /// Maintenance, such as checks and tests, always uses the endpoint. An S3 target becomes a freshly signed address
    /// each time, so it is never stored; a target of no network storage is returned as it is.
    /// </summary>
    /// <param name="target">The target the <c>.strm</c> file names.</param>
    /// <returns>The address, or <c>null</c> when the target needs an access and none covers it.</returns>
    string? ResolveShortcutTarget(string target);

    /// <summary>
    /// Gets the address a client reads an object at directly: the external address its storage is published at.
    /// </summary>
    /// <param name="target">The object, such as <c>s3://bucket/key</c>.</param>
    /// <returns>The address, or <c>null</c> when the storage is not public or is an archive, or no access covers it.</returns>
    Uri? GetClientAddress(string target);

    /// <summary>
    /// Gets how long an address <see cref="GetClientAddress"/> gives for an object stays valid at most.
    /// </summary>
    /// <param name="target">The object, such as <c>s3://bucket/key</c>.</param>
    /// <returns>The lifetime; zero when clients get no address, or one that does not expire.</returns>
    TimeSpan GetClientAddressLifetime(string target);

    /// <summary>
    /// Gets the address the server sends a request about an object to, for maintenance such as checks: the endpoint of
    /// its access, signed for the method.
    /// </summary>
    /// <param name="target">The object, such as <c>s3://bucket/key</c>.</param>
    /// <param name="method">The HTTP method the address is used with.</param>
    /// <returns>The address, or <c>null</c> when the target needs an access and none covers it.</returns>
    Uri? GetServerAddress(string target, string method);

    /// <summary>
    /// Lists the objects whose keys start with a prefix, in key order.
    /// </summary>
    /// <param name="prefix">The bucket and the prefix of the keys, such as <c>s3://bucket/hls/</c>.</param>
    /// <param name="limit">The most objects to list, or <c>null</c> for all.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The objects.</returns>
    /// <exception cref="ArgumentException">The prefix is not in storage the server can write.</exception>
    /// <exception cref="System.IO.IOException">No access covers the prefix, or the storage refused the listing or could not be reached.</exception>
    Task<IReadOnlyList<StorageObject>> ListAsync(string prefix, int? limit, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes objects; one that does not exist is deleted already.
    /// </summary>
    /// <param name="targets">The objects, such as <c>s3://bucket/key</c>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    /// <exception cref="ArgumentException">An object is not in storage the server can write.</exception>
    /// <exception cref="System.IO.IOException">No access covers an object, or the storage refused to delete one or could not be reached.</exception>
    Task DeleteAsync(IReadOnlyCollection<string> targets, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces the server's own address of a network storage source before the source is sent to a client:
    /// with the address the storage is published at, or, when it is not public, with none, so that the client
    /// has the server stream it. The server's address carries its credentials. A source in an archive is marked
    /// as not playable in any way.
    /// </summary>
    /// <param name="source">A source sent to a client.</param>
    void PrepareForClient(MediaSourceInfo source);

    /// <summary>
    /// Gets a value indicating whether a target lies in an archive, which clients cannot play from and the server
    /// neither streams nor transcodes for them. Probing and checks still read it. A <c>worker-local://</c> target, a file on
    /// the machine of an HLS worker, counts as archived too.
    /// </summary>
    /// <param name="target">The target a <c>.strm</c> file names, or <c>null</c>.</param>
    /// <returns><c>true</c> when an archived access covers the target, or it is on the machine of a worker.</returns>
    bool IsArchived(string? target);

    /// <summary>
    /// Gets every resource the library has in network storage, from what the library already knows: the items whose
    /// <c>.strm</c> file names a target.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The resources, with where they are.</returns>
    Task<IReadOnlyList<NetworkStorageResourceEntry>> GetResourceEntriesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the storage a root, such as that of an HLS store, is in; the root may be a whole bucket, such as <c>s3://bucket</c>.
    /// </summary>
    /// <param name="root">The root.</param>
    /// <returns>The storage, or <c>null</c> when no protocol knows the root.</returns>
    NetworkStorageRoot? GetStorageRoot(string root);

    /// <summary>
    /// Reports that reading the network resource of an item failed. The resource is checked: only a resource that is
    /// missing or not permitted is recorded, not one the storage was busy, failing or unreachable for.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="source">What found the failure.</param>
    /// <param name="target">The target that failed, when it may not be stored yet, as while probing a changed .strm; <c>null</c> for the stored one.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task ReportAccessFailureAsync(Guid itemId, NetworkStorageFailureSource source, string? target, CancellationToken cancellationToken);

    /// <summary>
    /// Checks every resource that can be checked: that it exists and has the size its <c>__true_cache__</c> entry
    /// declares. The failures recorded before are removed first.
    /// </summary>
    /// <param name="progress">The progress, in percent.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    Task CheckAllAsync(IProgress<double> progress, CancellationToken cancellationToken);
}
