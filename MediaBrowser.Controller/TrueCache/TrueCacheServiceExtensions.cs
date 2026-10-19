using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// Extensions of <see cref="ITrueCacheService"/>.
/// </summary>
public static class TrueCacheServiceExtensions
{
    /// <summary>
    /// Gets a value indicating whether the declaration of an item's resource changed after the item was last refreshed.
    /// The declaration replaces the resource, so every change monitor that watches the resource treats a changed declaration as a changed resource.
    /// </summary>
    /// <param name="trueCache">The <c>__true_cache__</c> lookup.</param>
    /// <param name="item">The item.</param>
    /// <returns><c>true</c> when a valid entry was declared anew or removed after the last refresh of the item.</returns>
    public static bool HasDeclarationChanged(this ITrueCacheService trueCache, BaseItem item)
        => !string.IsNullOrEmpty(item.Path) && trueCache.HasDeclarationChanged(item.Path, item.DateLastRefreshed);
}
