using MediaBrowser.Model.Hls;

namespace Jellyfin.MediaEncoding.Hls.Published;

/// <summary>
/// A published rendition of a content.
/// </summary>
/// <param name="Key">The key of the rendition.</param>
/// <param name="Base">The root of the HLS store the directory is in.</param>
/// <param name="RelativeDirectory">The directory, relative to the root.</param>
/// <param name="Summary">What the worker reported about it.</param>
public sealed record HlsPublishedRendition(string Key, string Base, string RelativeDirectory, HlsRenditionSummary Summary);
