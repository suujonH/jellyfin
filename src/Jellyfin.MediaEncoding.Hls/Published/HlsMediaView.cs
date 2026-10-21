using System.Collections.Generic;

namespace Jellyfin.MediaEncoding.Hls.Published;

/// <summary>
/// What is published of a content, as playback needs it. The preview is not part of it.
/// </summary>
/// <param name="Sha256">The hash of the content.</param>
/// <param name="MediaKey">The directory of the content in the HLS store.</param>
/// <param name="DurationSeconds">The duration of the content.</param>
/// <param name="VideoIndex">The ffprobe index of the video stream.</param>
/// <param name="ExpectedAudio">How many audio tracks the content has.</param>
/// <param name="Video">The video qualities, from the highest down.</param>
/// <param name="Audio">The audio tracks, by stream index.</param>
/// <param name="Subtitles">The subtitle tracks, by stream index.</param>
public sealed record HlsMediaView(
    string Sha256,
    string MediaKey,
    double DurationSeconds,
    int VideoIndex,
    int ExpectedAudio,
    IReadOnlyList<HlsPublishedRendition> Video,
    IReadOnlyList<HlsPublishedRendition> Audio,
    IReadOnlyList<HlsPublishedRendition> Subtitles)
{
    /// <summary>
    /// Gets a value indicating whether the content can be played: a video quality, and an audio track unless it has none.
    /// </summary>
    public bool Playable => Video.Count > 0 && (ExpectedAudio == 0 || Audio.Count > 0);
}
