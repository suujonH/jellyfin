using System;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// Extensions of <see cref="ITrueCacheService"/>.
/// </summary>
public static class TrueCacheServiceExtensions
{
    /// <summary>
    /// Gets a value indicating whether the declaration of an item's resource differs from the one the item was last refreshed with:
    /// a valid entry was added, removed, edited or replaced. Entry hashes are compared, so modification times do not matter
    /// and changes made while the server was not running are found as well.
    /// The declaration replaces the resource, so every change monitor that watches the resource treats a changed declaration as a changed resource.
    /// </summary>
    /// <param name="trueCache">The <c>__true_cache__</c> lookup.</param>
    /// <param name="item">The item.</param>
    /// <returns><c>true</c> when the hash of the valid entry, or its absence, differs from <see cref="BaseItem.TrueCacheHash"/>;
    /// <c>false</c> for a rejected entry, whose resource leaves the library instead.</returns>
    public static bool HasDeclarationChanged(this ITrueCacheService trueCache, BaseItem item)
    {
        if (string.IsNullOrEmpty(item.Path))
        {
            return false;
        }

        var lookup = trueCache.Lookup(item.Path);
        return lookup.State != TrueCacheState.Rejected
            && !string.Equals(lookup.Entry?.Hash, item.TrueCacheHash, StringComparison.Ordinal);
    }

    /// <summary>
    /// Gets the size the library records for a file: the size its valid entry declares, which for a .strm is the size of
    /// the media it names, or else the size of the file itself.
    /// </summary>
    /// <param name="trueCache">The <c>__true_cache__</c> lookup.</param>
    /// <param name="path">The file.</param>
    /// <param name="fileLength">The size of the file.</param>
    /// <returns>The size.</returns>
    public static long GetResourceSize(this ITrueCacheService trueCache, string path, long fileLength)
    {
        var lookup = trueCache.Lookup(path);
        return lookup.IsValid && lookup.Entry.MediaSize is { } size ? size : fileLength;
    }
}
