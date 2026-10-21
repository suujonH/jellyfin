using System;

namespace Jellyfin.MediaEncoding.Hls.Published;

/// <summary>
/// The encoding parameters of pre-encoded HLS that the server decides.
/// </summary>
public static class HlsEncodingProfile
{
    /// <summary>
    /// The version of the encoding parameters. It must change together with any of them and with any rule of the ladder:
    /// renditions published under another version are encoded again.
    /// </summary>
    public const string Version = "h264-aac-sdr-v3";

    /// <summary>
    /// The length of a segment, in seconds.
    /// </summary>
    public const double SegmentSeconds = 6;

    /// <summary>
    /// The interval of the forced keyframes, in seconds.
    /// </summary>
    public const double KeyframeSeconds = 2;

    /// <summary>
    /// The sample rate of the audio renditions. An AAC frame is 1024 samples long.
    /// </summary>
    public const int AudioSampleRate = 48000;

    /// <summary>
    /// Gets how many segments a video or audio rendition of a file is expected to have.
    /// </summary>
    /// <param name="durationSeconds">The duration of the file.</param>
    /// <returns>The number of segments, at least 1.</returns>
    public static int GetExpectedSegments(double durationSeconds)
        => (int)Math.Clamp(Math.Ceiling(durationSeconds / SegmentSeconds), 1, int.MaxValue);
}
