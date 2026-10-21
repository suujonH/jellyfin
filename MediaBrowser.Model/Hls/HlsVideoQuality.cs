namespace MediaBrowser.Model.Hls;

/// <summary>
/// A video quality of pre-encoded HLS.
/// </summary>
/// <param name="Name">The name of the quality: the height of its 16:9 box, such as 1080.</param>
/// <param name="Height">The height of the picture.</param>
/// <param name="Width">The width of the picture.</param>
/// <param name="FramesPerSecond">The frame rate.</param>
public sealed record HlsVideoQuality(int Name, int Height, int Width, double FramesPerSecond);
