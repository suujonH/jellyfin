namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// The state of the <c>__true_cache__</c> entry of a resource.
/// </summary>
public enum TrueCacheState
{
    /// <summary>The resource has no entry and is handled as usual.</summary>
    None,

    /// <summary>The entry is complete; the server uses it instead of reading the resource.</summary>
    Valid,

    /// <summary>The entry is invalid or incomplete; the resource is not added to the library.</summary>
    Rejected
}
