using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Hls;

namespace Jellyfin.MediaEncoding.Hls.Published;

/// <summary>
/// The video qualities of pre-encoded HLS.
/// </summary>
public static class HlsEncodingLadder
{
    /// <summary>
    /// Gets the names of the qualities, from the highest down.
    /// </summary>
    public static IReadOnlyList<int> Heights { get; } = [2160, 1440, 1080, 720, 480, 360, 240, 144];

    /// <summary>
    /// Selects the qualities a file is encoded in.
    /// </summary>
    /// <param name="probe">The probe of the file.</param>
    /// <param name="selectedHeights">The qualities chosen in the settings.</param>
    /// <returns>The qualities, from the highest down; none that would enlarge the picture by more than 5%.</returns>
    public static IReadOnlyList<HlsVideoQuality> Select(HlsProbe probe, IEnumerable<int> selectedHeights)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(selectedHeights);

        // Display size: the stored width corrected by the pixel aspect ratio, swapped for 90/270 degree rotation.
        var width = probe.Width * (double)probe.SarNumerator / probe.SarDenominator;
        var height = (double)probe.Height;
        if (Math.Abs(probe.Rotation % 180) == 90)
        {
            (width, height) = (height, width);
        }

        var portrait = height > width;
        var ladder = new List<HlsVideoQuality>();
        foreach (var name in selectedHeights.Distinct().OrderDescending().Where(Heights.Contains))
        {
            // A quality is a 16:9 box (9:16 for portrait material) that the picture is fitted into without padding.
            // Up to 5% of enlargement is tolerated and ignored (the picture keeps its own size); anything more is skipped.
            var boxWidth = (double)Even(name * 16.0 / 9);
            var boxHeight = (double)name;
            if (portrait)
            {
                (boxWidth, boxHeight) = (boxHeight, boxWidth);
            }

            var fit = Math.Min(boxWidth / width, boxHeight / height);
            if (fit > 1.05)
            {
                continue;
            }

            var scale = Math.Min(fit, 1);
            var outWidth = Even(width * scale);
            var outHeight = Even(height * scale);
            if (ladder.Any(q => q.Width == outWidth && q.Height == outHeight))
            {
                continue;
            }

            var fps = Math.Min(probe.FramesPerSecond, 60);
            if (name < 720 && fps > 30)
            {
                fps /= Math.Ceiling(fps / 30);
            }

            ladder.Add(new HlsVideoQuality(name, outHeight, outWidth, fps));
        }

        return ladder;
    }

    private static int Even(double x) => Math.Max(2, (int)Math.Round(x / 2, MidpointRounding.AwayFromZero) * 2);
}
