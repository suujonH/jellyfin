using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Jellyfin.MediaEncoding.Hls.Sessions;

/// <summary>
/// Reads the CSV list of the segment muxer while ffmpeg writes it. The muxer flushes each line before it closes the file the
/// line names, so a line is whole once it ends; the part of a line written so far is read again with the rest of it.
/// </summary>
internal sealed class HlsListReader
{
    private readonly string _path;
    private readonly List<HlsListEntry> _entries = [];
    private long _position;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsListReader"/> class.
    /// </summary>
    /// <param name="path">The path of the list, which need not exist yet.</param>
    public HlsListReader(string path)
    {
        _path = path;
    }

    /// <summary>
    /// Gets the lines read so far, in order.
    /// </summary>
    public IReadOnlyList<HlsListEntry> Entries => _entries;

    /// <summary>
    /// Reads the lines ended since the last read.
    /// </summary>
    /// <returns>Whether a line was added.</returns>
    /// <exception cref="InvalidDataException">A line is not <c>file,start,end</c>.</exception>
    public bool Read()
    {
        byte[] buffer;
        int length;
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= _position)
            {
                return false;
            }

            stream.Position = _position;
            buffer = new byte[stream.Length - _position];
            length = stream.ReadAtLeast(buffer, buffer.Length, false);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            // The file is busy; it is read again on the next change.
            return false;
        }

        var end = Array.LastIndexOf(buffer, (byte)'\n', length - 1);
        if (end < 0)
        {
            return false;
        }

        var count = _entries.Count;
        foreach (var line in Encoding.UTF8.GetString(buffer, 0, end).Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (trimmed.Length > 0)
            {
                _entries.Add(Parse(trimmed));
            }
        }

        _position += end + 1;
        return _entries.Count > count;
    }

    /// <summary>
    /// Parses a line of the list. The times are seconds printed to the microsecond.
    /// </summary>
    /// <param name="line">The line, without its end.</param>
    /// <returns>The entry.</returns>
    /// <exception cref="InvalidDataException">The line is not <c>file,start,end</c>.</exception>
    internal static HlsListEntry Parse(string line)
    {
        // The file name is the run's own and holds no comma, so the times are the last two fields.
        var endComma = line.LastIndexOf(',');
        var startComma = endComma > 0 ? line.LastIndexOf(',', endComma - 1) : -1;
        if (startComma <= 0
            || !TryParseTicks(line.AsSpan(startComma + 1, endComma - startComma - 1), out var start)
            || !TryParseTicks(line.AsSpan(endComma + 1), out var end))
        {
            throw new InvalidDataException("The segment list holds an invalid line: " + line);
        }

        return new HlsListEntry(line[..startComma], start, end);
    }

    private static bool TryParseTicks(ReadOnlySpan<char> seconds, out long ticks)
    {
        if (decimal.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && Math.Abs(value) < (decimal)TimeSpan.MaxValue.TotalSeconds)
        {
            ticks = decimal.ToInt64(Math.Round(value * TimeSpan.TicksPerSecond, MidpointRounding.AwayFromZero));
            return true;
        }

        ticks = 0;
        return false;
    }
}
