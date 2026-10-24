namespace MediaBrowser.Controller.NetworkStorage;

/// <summary>
/// The storage a root, such as that of an HLS store, is in.
/// </summary>
/// <param name="StorageKey">The key of the storage, such as <c>s3:bucket</c>.</param>
/// <param name="Protocol">The protocol.</param>
/// <param name="Container">The bucket or host.</param>
/// <param name="RequiresAccess">Whether the storage is read with an access.</param>
public sealed record NetworkStorageRoot(string StorageKey, string Protocol, string Container, bool RequiresAccess);
