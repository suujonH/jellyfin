using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Jellyfin.MediaEncoding.Hls.Tests.Ffmpeg;

/// <summary>
/// One line of the segment muxer's CSV list: <c>file,start,end</c>, in seconds of the output timeline.
/// </summary>
/// <param name="FileName">The segment file name.</param>
/// <param name="Start">The pts of the file's first reference packet; 0 for the first file of a run, whose start the muxer never records.</param>
/// <param name="End">The largest pts plus duration of the file's reference packets, which equals the next file's start.</param>
public sealed record SegmentListEntry(string FileName, double Start, double End)
{
    public static IReadOnlyList<SegmentListEntry> Read(string directory)
    {
        var entries = new List<SegmentListEntry>();
        foreach (var line in File.ReadAllLines(Path.Combine(directory, "list.csv")))
        {
            if (line.Length == 0)
            {
                continue;
            }

            var end = line.LastIndexOf(',');
            var start = line.LastIndexOf(',', end - 1);
            entries.Add(new SegmentListEntry(
                line[..start],
                double.Parse(line[(start + 1)..end], CultureInfo.InvariantCulture),
                double.Parse(line[(end + 1)..], CultureInfo.InvariantCulture)));
        }

        return entries;
    }
}
