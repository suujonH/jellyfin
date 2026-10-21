namespace MediaBrowser.Model.Hls;

/// <summary>
/// What a worker reports about an encoded rendition.
/// </summary>
/// <param name="Quality">The name of the video quality.</param>
/// <param name="Width">The width of the video.</param>
/// <param name="Height">The height of the video.</param>
/// <param name="FramesPerSecond">The frame rate of the video.</param>
/// <param name="Bandwidth">The peak bit rate.</param>
/// <param name="AverageBandwidth">The average bit rate.</param>
/// <param name="Codec">The RFC 6381 codec string, such as <c>avc1.640028</c>.</param>
/// <param name="SourceIndex">The ffprobe index of the audio or subtitle stream.</param>
/// <param name="Language">The language of the audio or subtitle track.</param>
/// <param name="Title">The title of the audio or subtitle track.</param>
/// <param name="Channels">The number of audio channels.</param>
/// <param name="Default">Whether the audio or subtitle track is the default one.</param>
public sealed record HlsRenditionSummary(
    int Quality = 0,
    int Width = 0,
    int Height = 0,
    double FramesPerSecond = 0,
    int Bandwidth = 0,
    int AverageBandwidth = 0,
    string Codec = "",
    int SourceIndex = -1,
    string Language = "",
    string Title = "",
    int Channels = 0,
    bool Default = false);
