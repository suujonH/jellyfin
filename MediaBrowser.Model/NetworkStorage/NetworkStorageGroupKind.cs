namespace MediaBrowser.Model.NetworkStorage;

/// <summary>
/// What a group of the resources of a storage is.
/// </summary>
public enum NetworkStorageGroupKind
{
    /// <summary>
    /// The resources of a library.
    /// </summary>
    Library = 0,

    /// <summary>
    /// The resources of a series.
    /// </summary>
    Series = 1,

    /// <summary>
    /// The contents no item refers to any longer.
    /// </summary>
    Orphans = 2
}
