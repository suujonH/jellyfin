using MediaBrowser.Model.Hls;

namespace Jellyfin.MediaEncoding.Hls.Published;

/// <summary>
/// A published rendition of a content.
/// </summary>
/// <param name="Key">The key of the rendition.</param>
/// <param name="RelativeDirectory">The directory in the HLS store.</param>
/// <param name="Summary">What the worker reported about it.</param>
public sealed record HlsPublishedRendition(string Key, string RelativeDirectory, HlsRenditionSummary Summary);
