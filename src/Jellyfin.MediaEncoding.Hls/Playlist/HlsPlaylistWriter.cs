using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Jellyfin.MediaEncoding.Hls.Plans;

namespace Jellyfin.MediaEncoding.Hls.Playlist;

/// <summary>
/// Writes HLS media playlists. Live HLS writes the durations of its plan, pre-encoded HLS the durations its worker measured.
/// </summary>
public static class HlsPlaylistWriter
{
    /// <summary>
    /// Writes the VOD media playlist of a plan.
    /// </summary>
    /// <param name="plan">The plan.</param>
    /// <param name="mapUri">The URI of the fMP4 init segment, or null for MPEG-TS segments.</param>
    /// <param name="getSegmentUri">Gets the URI of the segment with the given index.</param>
    /// <returns>The playlist.</returns>
    public static string WriteMedia(SegmentPlan plan, string? mapUri, Func<int, string> getSegmentUri)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return WriteMedia(plan.Segments.Select(s => s.DurationTicks).ToList(), mapUri, getSegmentUri);
    }

    /// <summary>
    /// Writes a VOD media playlist.
    /// </summary>
    /// <param name="segmentDurationTicks">The duration of every segment, in order.</param>
    /// <param name="mapUri">The URI of the fMP4 init segment, or null for MPEG-TS segments.</param>
    /// <param name="getSegmentUri">Gets the URI of the segment with the given index.</param>
    /// <returns>The playlist.</returns>
    public static string WriteMedia(IReadOnlyList<long> segmentDurationTicks, string? mapUri, Func<int, string> getSegmentUri)
    {
        ArgumentNullException.ThrowIfNull(segmentDurationTicks);
        ArgumentNullException.ThrowIfNull(getSegmentUri);
        ArgumentOutOfRangeException.ThrowIfZero(segmentDurationTicks.Count);

        // Every EXTINF rounded to the nearest second must not exceed the target duration; rounding the longest one up is safe.
        var longest = segmentDurationTicks.Max();
        var targetDuration = Math.Max(1, (longest + TimeSpan.TicksPerSecond - 1) / TimeSpan.TicksPerSecond);

        var builder = new StringBuilder(128 + (segmentDurationTicks.Count * 64));
        builder.AppendLine("#EXTM3U")
            .AppendLine("#EXT-X-PLAYLIST-TYPE:VOD")
            .Append("#EXT-X-VERSION:")
            .Append(mapUri is null ? "3" : "7")
            .AppendLine()
            .Append("#EXT-X-TARGETDURATION:")
            .Append(targetDuration.ToString(CultureInfo.InvariantCulture))
            .AppendLine()
            .AppendLine("#EXT-X-MEDIA-SEQUENCE:0");

        if (mapUri is not null)
        {
            // Init file that only includes fMP4 headers
            builder.Append("#EXT-X-MAP:URI=\"")
                .Append(mapUri)
                .Append('"')
                .AppendLine();
        }

        for (var i = 0; i < segmentDurationTicks.Count; i++)
        {
            builder.Append("#EXTINF:")
                .Append(((decimal)segmentDurationTicks[i] / TimeSpan.TicksPerSecond).ToString("0.000000", CultureInfo.InvariantCulture))
                .AppendLine(", nodesc")
                .Append(getSegmentUri(i))
                .AppendLine();
        }

        builder.AppendLine("#EXT-X-ENDLIST");
        return builder.ToString();
    }
}
