namespace MediaBrowser.Model.NetworkStorage;

/// <summary>
/// Why a resource of network storage is not valid.
/// </summary>
public enum NetworkStorageIssue
{
    /// <summary>
    /// No access covers the storage the resource is in.
    /// </summary>
    NoAccess = 0,

    /// <summary>
    /// The storage answered that the resource does not exist.
    /// </summary>
    NotFound = 1,

    /// <summary>
    /// The storage refused to give the resource.
    /// </summary>
    Forbidden = 2,

    /// <summary>
    /// The resource is not the size its <c>__true_cache__</c> entry declares.
    /// </summary>
    SizeMismatch = 3,

    /// <summary>
    /// The <c>.strm</c> file names no address the server can read, or the item was not probed yet.
    /// </summary>
    Unresolved = 4,

    /// <summary>
    /// The resource has no <c>__true_cache__</c> entry, so what it should be is not known.
    /// </summary>
    Undeclared = 5
}
