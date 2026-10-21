namespace MediaBrowser.Controller.NetworkStorage;

/// <summary>
/// Where in network storage the target of a <c>.strm</c> file is.
/// </summary>
/// <param name="Protocol">The protocol, one of <see cref="MediaBrowser.Model.NetworkStorage.NetworkStorageProtocols"/>.</param>
/// <param name="Container">The bucket or host the resource is in.</param>
/// <param name="ObjectPath">The object key or the path on the host.</param>
/// <param name="Target">The target as the <c>.strm</c> file names it.</param>
public sealed record StorageLocation(string Protocol, string Container, string ObjectPath, string Target)
{
    /// <summary>
    /// Gets the key of the storage, such as <c>s3:bucket</c>, which groups its resources.
    /// </summary>
    public string StorageKey => GetStorageKey(Protocol, Container);

    /// <summary>
    /// Gets the key of a storage.
    /// </summary>
    /// <param name="protocol">The protocol.</param>
    /// <param name="container">The bucket or host.</param>
    /// <returns>The key.</returns>
    public static string GetStorageKey(string protocol, string container) => protocol + ":" + container;
}
