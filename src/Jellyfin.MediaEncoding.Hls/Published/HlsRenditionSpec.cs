using MediaBrowser.Model.Hls;

namespace Jellyfin.MediaEncoding.Hls.Published;

/// <summary>
/// A rendition the content of a file should have.
/// </summary>
/// <param name="Key">The key of the rendition: <c>v{quality}</c>, <c>a{index}</c>, <c>s{index}</c> or <c>preview</c>.</param>
/// <param name="Kind">What the rendition holds.</param>
/// <param name="Video">The video quality, for a video rendition.</param>
/// <param name="SourceIndex">The ffprobe index of the audio or subtitle stream, otherwise -1.</param>
/// <param name="Channels">The number of audio channels, otherwise 0.</param>
public sealed record HlsRenditionSpec(string Key, HlsRenditionKind Kind, HlsVideoQuality? Video, int SourceIndex, int Channels);
