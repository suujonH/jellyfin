namespace MediaBrowser.Controller.NetworkStorage;

/// <summary>
/// The outcome of reading the headers of a resource.
/// </summary>
public enum ObjectCheckStatus
{
    /// <summary>
    /// The resource exists and, if its size is declared, has that size.
    /// </summary>
    Ok = 0,

    /// <summary>
    /// The resource does not exist.
    /// </summary>
    NotFound = 1,

    /// <summary>
    /// Reading the resource is not permitted.
    /// </summary>
    Forbidden = 2,

    /// <summary>
    /// The resource is not the size its <c>__true_cache__</c> entry declares.
    /// </summary>
    SizeMismatch = 3,

    /// <summary>
    /// The storage was busy, failed or could not be reached; nothing is known about the resource.
    /// </summary>
    Transient = 4
}
