using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Model.Hls;

namespace Jellyfin.MediaEncoding.Hls.Published;

/// <summary>
/// Decides which renditions the content of a file should have.
/// </summary>
public static class HlsRenditionPlanner
{
    /// <summary>
    /// Gets the subtitle codecs that can be converted to WebVTT. Graphic subtitles (pgs, dvd, dvb) are skipped.
    /// </summary>
    public static IReadOnlyList<string> TextSubtitleCodecs { get; } = ["subrip", "srt", "ass", "ssa", "webvtt", "mov_text", "text"];

    /// <summary>
    /// Gets the renditions the content of a file should have.
    /// </summary>
    /// <param name="probe">The probe of the file.</param>
    /// <param name="qualities">The qualities chosen in the settings.</param>
    /// <returns>The video qualities from the highest down, then every audio track, the text subtitles and the preview;
    /// nothing at all when no video quality fits.</returns>
    public static IReadOnlyList<HlsRenditionSpec> GetDesired(HlsProbe probe, IEnumerable<int> qualities)
    {
        ArgumentNullException.ThrowIfNull(probe);

        var video = HlsEncodingLadder.Select(probe, qualities);
        if (video.Count == 0)
        {
            return [];
        }

        return
        [
            .. video.Select(q => new HlsRenditionSpec("v" + q.Name.ToString(CultureInfo.InvariantCulture), HlsRenditionKind.Video, q, -1, 0)),
            .. probe.Audio.Select(a => new HlsRenditionSpec("a" + a.Index.ToString(CultureInfo.InvariantCulture), HlsRenditionKind.Audio, null, a.Index, Math.Clamp(a.Channels, 1, 2))),
            .. probe.Subtitles.Where(s => TextSubtitleCodecs.Contains(s.Codec)).Select(s => new HlsRenditionSpec("s" + s.Index.ToString(CultureInfo.InvariantCulture), HlsRenditionKind.Subtitle, null, s.Index, 0)),
            new HlsRenditionSpec("preview", HlsRenditionKind.Preview, null, -1, 0)
        ];
    }
}
