namespace MediaBrowser.Model.NetworkStorage;

/// <summary>
/// Whether a resource of network storage can be read, as far as the server knows without reading it.
/// </summary>
public enum NetworkStorageResourceState
{
    /// <summary>
    /// The resource is within an access and nothing showed it cannot be read.
    /// </summary>
    Valid = 0,

    /// <summary>
    /// The resource is outside every access, or reading it failed because it is missing or not permitted.
    /// </summary>
    Invalid = 1,

    /// <summary>
    /// Nothing about the resource is declared, so its state cannot be told without reading it.
    /// </summary>
    Unknown = 2
}
