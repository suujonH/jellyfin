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
    /// Gets the resources that were rejected because their entry is invalid or incomplete.
    /// </summary>
    /// <returns>The rejections, most recent first.</returns>
    IReadOnlyList<TrueCacheRejection> GetRejections();
}
