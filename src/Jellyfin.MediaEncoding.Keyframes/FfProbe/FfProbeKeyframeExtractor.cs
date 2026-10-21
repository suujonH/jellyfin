using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace Jellyfin.MediaEncoding.Keyframes.FfProbe;

/// <summary>
/// FfProbe based keyframe extractor.
/// </summary>
public static class FfProbeKeyframeExtractor
{
    /// <summary>
    /// Extracts the keyframes of the first video stream that is no cover art using the ffprobe executable at the specified path.
    /// The keyframes are relative to the format start time, the timeline of ffmpeg <c>-copyts -start_at_zero</c>.
    /// </summary>
    /// <param name="ffProbePath">The path to the ffprobe executable.</param>
    /// <param name="filePath">The file path.</param>
    /// <returns>An instance of <see cref="KeyframeData"/>.</returns>
    public static KeyframeData GetKeyframeData(string ffProbePath, string filePath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = ffProbePath,
                Arguments = string.Format(
                    CultureInfo.InvariantCulture,
                    "-fflags +genpts -v error -skip_frame nokey -show_entries format=start_time,duration -show_entries stream=duration -show_entries packet=pts_time,flags -select_streams V:0 -of csv \"{0}\"",
                    filePath),

                CreateNoWindow = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                RedirectStandardOutput = true,

                WindowStyle = ProcessWindowStyle.Hidden,
                ErrorDialog = false,
            },
            EnableRaisingEvents = true
        };

        try
        {
            process.Start();
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch
            {
                // We do not care if process priority setting fails
                // Ideally log a warning but this does not have a logger available
            }

            return ParseStream(process.StandardOutput);
        }
        catch (Exception)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill();
                }
            }
            catch
            {
                // We do not care if this fails
            }

            throw;
        }
    }

    internal static KeyframeData ParseStream(StreamReader reader)
    {
        var keyframes = new List<long>();
        double streamDuration = 0;
        double formatDuration = 0;
        double formatStart = 0;

        using (reader)
        {
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine().AsSpan();
                if (line.IsEmpty)
                {
                    continue;
                }

                var firstComma = line.IndexOf(',');
                var lineType = line[..firstComma];
                var rest = line[(firstComma + 1)..];
                if (lineType.Equals("packet", StringComparison.OrdinalIgnoreCase))
                {
                    // Split time and flags from the packet line. Example line: packet,7169.079000,K_
                    var secondComma = rest.IndexOf(',');
                    var ptsTime = rest[..secondComma];
                    var flags = rest[(secondComma + 1)..];
                    if (flags.StartsWith("K_"))
                    {
                        if (double.TryParse(ptsTime, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var keyframe))
                        {
                            // Have to manually convert to ticks to avoid rounding errors as TimeSpan is only precise down to 1 ms when converting double.
                            keyframes.Add(Convert.ToInt64(keyframe * TimeSpan.TicksPerSecond));
                        }
                    }
                }
                else if (lineType.Equals("stream", StringComparison.OrdinalIgnoreCase))
                {
                    if (double.TryParse(rest, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var streamDurationResult))
                    {
                        streamDuration = streamDurationResult;
                    }
                }
                else if (lineType.Equals("format", StringComparison.OrdinalIgnoreCase))
                {
                    // format,start_time,duration; a line with the duration alone has no start time to remove.
                    var comma = rest.IndexOf(',');
                    if (comma >= 0
                        && double.TryParse(rest[..comma], NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var formatStartResult))
                    {
                        formatStart = formatStartResult;
                    }

                    if (double.TryParse(rest[(comma + 1)..], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var formatDurationResult))
                    {
                        formatDuration = formatDurationResult;
                    }
                }
            }

            // ffprobe prints the format section after the packets. MPEG-TS and similar containers start at an arbitrary
            // timestamp, which ffmpeg removes when it seeks and with -start_at_zero.
            var startTicks = Convert.ToInt64(formatStart * TimeSpan.TicksPerSecond);
            for (var i = 0; i < keyframes.Count; i++)
            {
                keyframes[i] -= startTicks;
            }

            // Prefer the stream duration as it should be more accurate
            var duration = streamDuration > 0 ? streamDuration : formatDuration;

            return new KeyframeData(TimeSpan.FromSeconds(duration).Ticks, keyframes);
        }
    }
}
