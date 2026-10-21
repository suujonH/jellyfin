using System.Collections.Generic;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// What ffprobe found in a media file, as far as pre-encoding needs it.
/// </summary>
/// <param name="Width">The width of the video, in stored pixels.</param>
/// <param name="Height">The height of the video.</param>
/// <param name="FramesPerSecond">The average frame rate of the video.</param>
/// <param name="DurationSeconds">The duration of the file.</param>
/// <param name="VideoCodec">The codec of the video.</param>
/// <param name="PixelFormat">The pixel format of the video.</param>
/// <param name="ColorTransfer">The transfer characteristics of the video, such as <c>smpte2084</c>.</param>
/// <param name="FieldOrder">The field order of the video, such as <c>progressive</c> or <c>tt</c>.</param>
/// <param name="Rotation">The rotation of the video, in degrees.</param>
/// <param name="Audio">The audio tracks.</param>
/// <param name="Subtitles">The subtitle tracks.</param>
/// <param name="VideoIndex">The ffprobe index of the video stream.</param>
/// <param name="SarNumerator">The numerator of the sample aspect ratio.</param>
/// <param name="SarDenominator">The denominator of the sample aspect ratio.</param>
public sealed record HlsProbe(
    int Width,
    int Height,
    double FramesPerSecond,
    double DurationSeconds,
    string VideoCodec,
    string PixelFormat,
    string ColorTransfer,
    string FieldOrder,
    int Rotation,
    IReadOnlyList<HlsAudioTrack> Audio,
    IReadOnlyList<HlsSubtitleTrack> Subtitles,
    int VideoIndex = 0,
    int SarNumerator = 1,
    int SarDenominator = 1);
