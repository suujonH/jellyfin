namespace MediaBrowser.Model.Hls;

/// <summary>
/// A rendition an encode plan asks for.
/// </summary>
/// <param name="Key">The key of the rendition, such as <c>v1080</c>, <c>a1</c>, <c>s2</c> or <c>preview</c>.</param>
/// <param name="Kind">What the rendition holds.</param>
/// <param name="RelativeDirectory">The directory of the rendition in the HLS store.</param>
/// <param name="Video">The video quality, for a video rendition.</param>
/// <param name="SourceIndex">The ffprobe index of the audio or subtitle stream, otherwise -1.</param>
/// <param name="Channels">The number of audio channels, otherwise 0.</param>
public sealed record HlsPlannedRendition(string Key, HlsRenditionKind Kind, string RelativeDirectory, HlsVideoQuality? Video, int SourceIndex, int Channels);
