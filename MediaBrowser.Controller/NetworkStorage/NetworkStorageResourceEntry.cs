using System;
using MediaBrowser.Model.NetworkStorage;

namespace MediaBrowser.Controller.NetworkStorage;

/// <summary>
/// A resource as the network storage describes it, with where it is.
/// </summary>
/// <param name="Resource">The resource.</param>
/// <param name="StorageKey">The key of the storage its target is in.</param>
/// <param name="Protocol">The protocol of the storage; empty when not resolved.</param>
/// <param name="Container">The bucket or host; empty when not resolved.</param>
/// <param name="RequiresAccess">Whether the storage is read with an access.</param>
/// <param name="TopParentId">The top folder of the item, a folder of its library.</param>
public sealed record NetworkStorageResourceEntry(NetworkStorageResource Resource, string StorageKey, string Protocol, string Container, bool RequiresAccess, Guid? TopParentId);
