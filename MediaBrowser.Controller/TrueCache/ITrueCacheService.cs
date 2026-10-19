using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// Reads <c>__true_cache__</c> entries. A resource with an entry is never read by the server, except to deliver it
/// to a client or to transcode it; everything else comes from the entry. A resource whose entry is incomplete is rejected.
/// </summary>
public interface ITrueCacheService
{
    /// <summary>
    /// Looks up the entry of a resource. Results are kept until the declaration or the resource changes.
    /// </summary>
    /// <param name="resourcePath">The absolute path of the media file or disc folder.</param>
    /// <returns>The lookup result.</returns>
    TrueCacheLookup Lookup(string resourcePath);

    /// <summary>
    /// Validates the entry of a resource again, ignoring kept results.
    /// </summary>
    /// <param name="resourcePath">The absolute path of the media file or disc folder.</param>
    /// <returns>The lookup result.</returns>
    TrueCacheLookup Validate(string resourcePath);

    /// <summary>
    /// Gets the album-wide entry of an album folder.
    /// </summary>
    /// <param name="albumPath">The album folder.</param>
    /// <returns>The lookup result.</returns>
    TrueCacheLookup LookupAlbum(string albumPath);

    /// <summary>
    /// Gets a value indicating whether the declaration of a resource changed after a point in time: its valid entry was
    /// declared anew, or a valid entry was removed. A removed entry leaves nothing on disk, so a removal is only known
    /// when the server saw the entry before; otherwise the scheduled tasks restore what the entry provided.
    /// </summary>
    /// <param name="resourcePath">The absolute path of the media file or disc folder.</param>
    /// <param name="since">The point in time, usually the last refresh of the item.</param>
    /// <returns><c>true</c> when the declaration changed after <paramref name="since"/>.</returns>
    bool HasDeclarationChanged(string resourcePath, DateTime since);

    /// <summary>
    /// Gets the resources that were rejected because their entry is invalid or incomplete.
    /// </summary>
    /// <returns>The rejections, most recent first.</returns>
    IReadOnlyList<TrueCacheRejection> GetRejections();
}
