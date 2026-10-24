using MediaBrowser.Model.Hls;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A published rendition of a content, with the size of its files.
/// </summary>
/// <param name="Key">The key, such as <c>v1080</c>.</param>
/// <param name="Kind">The kind.</param>
/// <param name="Base">The root of the HLS store it is in.</param>
/// <param name="Summary">What the worker reported about it.</param>
/// <param name="Size">The size of its files, in bytes.</param>
/// <param name="FileCount">The number of its files.</param>
public sealed record HlsStoredRendition(string Key, HlsRenditionKind Kind, string Base, HlsRenditionSummary Summary, long Size, int FileCount);
