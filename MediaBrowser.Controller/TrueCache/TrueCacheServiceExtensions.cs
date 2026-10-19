using System.IO;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// Extensions of <see cref="ITrueCacheService"/>.
/// </summary>
public static class TrueCacheServiceExtensions
{
    /// <summary>
    /// Gets a value indicating whether the resource of an item was declared anew after the item was last refreshed.
    /// The declaration replaces the resource, so every change monitor that watches the resource treats a changed declaration as a changed resource.
    /// </summary>
    /// <param name="trueCache">The <c>__true_cache__</c> lookup.</param>
    /// <param name="item">The item.</param>
    /// <returns><c>true</c> when the <c>meta.toml</c> of a valid entry is newer than the last refresh of the item.</returns>
    public static bool HasDeclarationChanged(this ITrueCacheService trueCache, BaseItem item)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return false;
        }

        var lookup = trueCache.Lookup(item.Path);
        if (!lookup.IsValid)
        {
            return false;
        }

        var meta = new FileInfo(Path.Combine(lookup.Entry.Directory, TrueCacheLayout.MetaFile));
        return meta.Exists && meta.LastWriteTimeUtc > item.DateLastRefreshed;
    }
}
