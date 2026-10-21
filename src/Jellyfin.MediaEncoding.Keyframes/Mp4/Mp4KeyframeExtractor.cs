using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace Jellyfin.MediaEncoding.Keyframes.Mp4;

/// <summary>
/// The keyframe extractor for the MP4 and QuickTime containers. It reads the sample tables in the <c>moov</c> box, never the media data.
/// </summary>
/// <remarks>
/// Times follow the ffmpeg mov demuxer: a sample is presented at its decode time plus its composition offset. A track with an
/// edit list starts at the end of its leading empty edits and presents its media edit from there, from the media time of the
/// edit or, for video, from the earliest frame in it; a track without one starts at its first sample. The result is relative to
/// the earliest audio or video track, which is what ffmpeg reports as the format start time.
/// </remarks>
public static class Mp4KeyframeExtractor
{
    // The moov box of a long film is a few megabytes.
    private const long MaxMovieBoxSize = 256L * 1024 * 1024;

    // Far more samples than any film has; a larger count is a broken table.
    private const long MaxSamples = 100_000_000;

    private static readonly uint _moov = BinaryPrimitives.ReadUInt32BigEndian("moov"u8);
    private static readonly uint _mvhd = BinaryPrimitives.ReadUInt32BigEndian("mvhd"u8);
    private static readonly uint _mvex = BinaryPrimitives.ReadUInt32BigEndian("mvex"u8);
    private static readonly uint _trak = BinaryPrimitives.ReadUInt32BigEndian("trak"u8);
    private static readonly uint _edts = BinaryPrimitives.ReadUInt32BigEndian("edts"u8);
    private static readonly uint _elst = BinaryPrimitives.ReadUInt32BigEndian("elst"u8);
    private static readonly uint _mdia = BinaryPrimitives.ReadUInt32BigEndian("mdia"u8);
    private static readonly uint _mdhd = BinaryPrimitives.ReadUInt32BigEndian("mdhd"u8);
    private static readonly uint _hdlr = BinaryPrimitives.ReadUInt32BigEndian("hdlr"u8);
    private static readonly uint _minf = BinaryPrimitives.ReadUInt32BigEndian("minf"u8);
    private static readonly uint _stbl = BinaryPrimitives.ReadUInt32BigEndian("stbl"u8);
    private static readonly uint _stts = BinaryPrimitives.ReadUInt32BigEndian("stts"u8);
    private static readonly uint _ctts = BinaryPrimitives.ReadUInt32BigEndian("ctts"u8);
    private static readonly uint _stss = BinaryPrimitives.ReadUInt32BigEndian("stss"u8);
    private static readonly uint _stps = BinaryPrimitives.ReadUInt32BigEndian("stps"u8);
    private static readonly uint _stsz = BinaryPrimitives.ReadUInt32BigEndian("stsz"u8);
    private static readonly uint _stz2 = BinaryPrimitives.ReadUInt32BigEndian("stz2"u8);
    private static readonly uint _sbgp = BinaryPrimitives.ReadUInt32BigEndian("sbgp"u8);
    private static readonly uint _rap = BinaryPrimitives.ReadUInt32BigEndian("rap "u8);
    private static readonly uint _sync = BinaryPrimitives.ReadUInt32BigEndian("sync"u8);
    private static readonly uint _vide = BinaryPrimitives.ReadUInt32BigEndian("vide"u8);
    private static readonly uint _soun = BinaryPrimitives.ReadUInt32BigEndian("soun"u8);

    /// <summary>
    /// Extracts the keyframes in ticks, relative to the format start time, from the sample tables of the first video track.
    /// </summary>
    /// <param name="filePath">The file path.</param>
    /// <returns>An instance of <see cref="KeyframeData"/> whose duration runs from the format start to the end of the latest audio or video track, as ffprobe reports it; without keyframes when the file keeps samples outside the <c>moov</c> box (fragmented MP4), uses edit lists ffmpeg would play differently, lists partial sync samples, or has another track starting before the audio and video.</returns>
    public static KeyframeData GetKeyframeData(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return GetKeyframeData(stream);
    }

    internal static KeyframeData GetKeyframeData(Stream stream)
    {
        var movie = ReadBoxes(ReadMovieBox(stream));
        var mvhd = Find(movie, _mvhd) ?? throw new FormatException("The moov box has no mvhd box.");
        var movieTimescale = ReadTimescale(mvhd);

        // A fragmented file can keep its first samples in the moov box and the rest in moof boxes.
        if (Find(movie, _mvex) is not null)
        {
            return new KeyframeData(0, []);
        }

        Track? video = null;
        long? formatStart = null;
        long? otherStart = null;
        long tracksEnd = 0;
        foreach (var (type, trak) in movie)
        {
            if (type != _trak)
            {
                continue;
            }

            var track = ReadTrack(trak, movieTimescale, video is null);
            if (track is null)
            {
                continue;
            }

            if (track.Value.IsUnsupported)
            {
                return new KeyframeData(0, []);
            }

            if (!track.Value.IsMedia)
            {
                if (track.Value.StartTicks.HasValue)
                {
                    otherStart = Math.Min(otherStart ?? long.MaxValue, track.Value.StartTicks.Value);
                }

                continue;
            }

            if (track.Value.StartTicks.HasValue)
            {
                formatStart = Math.Min(formatStart ?? long.MaxValue, track.Value.StartTicks.Value);
            }

            tracksEnd = Math.Max(tracksEnd, track.Value.EndTicks);
            if (video is null && track.Value.Keyframes is not null)
            {
                video = track;
            }
        }

        // ffmpeg starts the format at a subtitle, chapter or timecode track less than a second before the audio and video when it
        // reads the track while probing; such a file is left to ffprobe.
        var start = formatStart ?? 0;
        if (otherStart < start)
        {
            return new KeyframeData(0, []);
        }

        // ffprobe takes the duration from the tracks, not from the movie header.
        var duration = Math.Max(0, tracksEnd - start);
        if (video is null || video.Value.Keyframes!.Count == 0)
        {
            return new KeyframeData(duration, []);
        }

        var keyframes = video.Value.Keyframes!;
        for (var i = 0; i < keyframes.Count; i++)
        {
            keyframes[i] -= start;
        }

        keyframes.Sort();
        return new KeyframeData(duration, keyframes);
    }

    // Reads the payload of the top-level moov box, which can follow the media data.
    private static ReadOnlyMemory<byte> ReadMovieBox(Stream stream)
    {
        var length = stream.Length;
        Span<byte> header = stackalloc byte[16];
        long position = 0;
        while (position + 8 <= length)
        {
            stream.Position = position;
            stream.ReadExactly(header[..8]);
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = BinaryPrimitives.ReadUInt32BigEndian(header[4..]);
            var headerSize = 8;
            if (size == 1)
            {
                stream.ReadExactly(header[8..]);
                var largeSize = BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
                if (largeSize > long.MaxValue)
                {
                    throw new FormatException("A box is larger than the file.");
                }

                size = (long)largeSize;
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = length - position;
            }

            if (size < headerSize || size > length - position)
            {
                throw new FormatException("A box is larger than the file.");
            }

            if (type == _moov)
            {
                var payloadSize = size - headerSize;
                if (payloadSize > MaxMovieBoxSize)
                {
                    throw new FormatException("The moov box is too large.");
                }

                var payload = new byte[payloadSize];
                stream.Position = position + headerSize;
                stream.ReadExactly(payload);
                return payload;
            }

            position += size;
        }

        throw new FormatException("The file has no moov box.");
    }

    // Splits a box payload into its child boxes.
    private static List<(uint Type, ReadOnlyMemory<byte> Payload)> ReadBoxes(ReadOnlyMemory<byte> data)
    {
        var boxes = new List<(uint Type, ReadOnlyMemory<byte> Payload)>();
        var span = data.Span;
        var position = 0;
        while (position + 8 <= span.Length)
        {
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(span[position..]);
            var type = BinaryPrimitives.ReadUInt32BigEndian(span[(position + 4)..]);
            var headerSize = 8;
            if (size == 1)
            {
                if (position + 16 > span.Length)
                {
                    throw new FormatException("A box header is truncated.");
                }

                var largeSize = BinaryPrimitives.ReadUInt64BigEndian(span[(position + 8)..]);
                size = largeSize > int.MaxValue ? long.MaxValue : (long)largeSize;
                headerSize = 16;
            }
            else if (size == 0)
            {
                size = span.Length - position;
            }

            if (size < headerSize || size > span.Length - position)
            {
                throw new FormatException("A box is larger than its parent.");
            }

            boxes.Add((type, data.Slice(position + headerSize, (int)size - headerSize)));
            position += (int)size;
        }

        return boxes;
    }

    private static ReadOnlyMemory<byte>? Find(List<(uint Type, ReadOnlyMemory<byte> Payload)> boxes, uint type)
    {
        foreach (var box in boxes)
        {
            if (box.Type == type)
            {
                return box.Payload;
            }
        }

        return null;
    }

    // mvhd and mdhd share the layout of their first fields: version and flags, two dates, the timescale, the duration.
    private static uint ReadTimescale(ReadOnlyMemory<byte> header)
    {
        var span = header.Span;
        var timescale = span.Length > 0 && span[0] == 1
            ? BinaryPrimitives.ReadUInt32BigEndian(span[20..])
            : BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
        return timescale == 0 ? throw new FormatException("A timescale is 0.") : timescale;
    }

    private static ulong ReadDuration(ReadOnlyMemory<byte> header)
    {
        var span = header.Span;
        if (span.Length > 0 && span[0] == 1)
        {
            var duration = BinaryPrimitives.ReadUInt64BigEndian(span[24..]);
            return duration == ulong.MaxValue ? 0 : duration;
        }

        var shortDuration = BinaryPrimitives.ReadUInt32BigEndian(span[16..]);
        return shortDuration == uint.MaxValue ? 0 : shortDuration;
    }

    // Reads a track; null for a track without a sample table.
    private static Track? ReadTrack(ReadOnlyMemory<byte> trak, uint movieTimescale, bool collectKeyframes)
    {
        var trakBoxes = ReadBoxes(trak);
        if (Find(trakBoxes, _mdia) is not { } mdia)
        {
            return null;
        }

        var mdiaBoxes = ReadBoxes(mdia);
        if (Find(mdiaBoxes, _hdlr) is not { } hdlr || hdlr.Length < 12)
        {
            return null;
        }

        var handler = BinaryPrimitives.ReadUInt32BigEndian(hdlr.Span[8..]);
        var isVideo = handler == _vide;
        var isMedia = isVideo || handler == _soun;

        var mdhd = Find(mdiaBoxes, _mdhd) ?? throw new FormatException("A track has no mdhd box.");
        var timescale = ReadTimescale(mdhd);
        var mediaDuration = (long)Math.Min(ReadDuration(mdhd), (ulong)long.MaxValue);

        if (!TryReadEdit(Find(trakBoxes, _edts), movieTimescale, timescale, out var emptyDuration, out var mediaTime, out var editDuration))
        {
            return Track.Unsupported;
        }

        var stbl = Find(mdiaBoxes, _minf) is { } minf ? Find(ReadBoxes(minf), _stbl) : null;
        if (stbl is null)
        {
            return null;
        }

        var stblBoxes = ReadBoxes(stbl.Value);
        if (Find(stblBoxes, _stts) is not { } stts)
        {
            return null;
        }

        var keyframes = isVideo && collectKeyframes ? new List<long>() : null;

        // ffmpeg reads more keyframes than the sync samples from a partial sync table, where QuickTime lists the open-GOP
        // keyframes of MPEG-2, and from random access sample groups, which mark those of HEVC. It skips empty samples, which are
        // looked up in the stsz box; the compact stz2 box is not read.
        if (keyframes is not null
            && (Find(stblBoxes, _stps) is not null || Find(stblBoxes, _stz2) is not null || HasKeyframeGroup(stblBoxes)))
        {
            return Track.Unsupported;
        }

        var ctts = Find(stblBoxes, _ctts) ?? ReadOnlyMemory<byte>.Empty;
        var stsz = keyframes is not null ? Find(stblBoxes, _stsz) ?? ReadOnlyMemory<byte>.Empty : ReadOnlyMemory<byte>.Empty;

        // The samples presented within the media edit; without an edit list, all of them.
        var editStart = editDuration.HasValue ? mediaTime : long.MinValue;
        var editEnd = editDuration.HasValue ? mediaTime + editDuration.Value : long.MaxValue;
        var firstPresentation = ReadSamples(
            stts.Span,
            ctts.Span,
            isVideo ? Find(stblBoxes, _stss) : null,
            stsz.Span,
            editStart,
            editEnd,
            keyframes,
            out var editOrigin,
            out var sampleDuration);

        // ffmpeg starts a track with an edit list at the end of its empty edits, whatever the samples, and one without a list at
        // its first sample, even when a later sample is presented earlier. A track without samples, as in a fragmented file, has
        // no start.
        long? startTicks = null;
        if (firstPresentation.HasValue)
        {
            startTicks = ToTicks(editDuration.HasValue ? emptyDuration : firstPresentation.Value, timescale);
        }

        if (keyframes is not null)
        {
            // ffmpeg presents the media edit from the end of the empty edits. It drops what precedes the media time, and moves an
            // edited video so that its earliest frame in the edit is presented there, as when the edit starts at media time 0
            // despite the B-frame delay.
            var origin = editDuration.HasValue ? editOrigin ?? mediaTime : 0;
            for (var i = 0; i < keyframes.Count; i++)
            {
                // ffmpeg drops the frames outside the media edit, so a keyframe there is no cut point.
                if (keyframes[i] < editStart || keyframes[i] >= editEnd)
                {
                    return Track.Unsupported;
                }

                keyframes[i] = ToTicks(emptyDuration + keyframes[i] - origin, timescale);
            }
        }

        // ffmpeg ends a stream its duration after its start. The duration is that of the media header, capped by the samples and,
        // with an edit list, by the sum of the edits, the empty ones included.
        var duration = sampleDuration > 0 ? Math.Min(mediaDuration, sampleDuration) : mediaDuration;
        if (editDuration.HasValue)
        {
            duration = Math.Min(duration, checked(emptyDuration + editDuration.Value));
        }

        return new Track(false, isMedia, (startTicks ?? ToTicks(emptyDuration, timescale)) + ToTicks(duration, timescale), startTicks, keyframes);
    }

    // Whether a sample-to-group box marks random access points, which ffmpeg reads as keyframes.
    private static bool HasKeyframeGroup(List<(uint Type, ReadOnlyMemory<byte> Payload)> stblBoxes)
    {
        foreach (var (type, payload) in stblBoxes)
        {
            if (type != _sbgp || payload.Length < 8)
            {
                continue;
            }

            var groupingType = BinaryPrimitives.ReadUInt32BigEndian(payload.Span[4..]);
            if (groupingType == _rap || groupingType == _sync)
            {
                return true;
            }
        }

        return false;
    }

    // Reads the edit list in the media timescale: the duration of the leading empty edits, and the media time and duration of the
    // media edit, which is null without a list. Only leading empty edits followed by one media edit at the normal rate are read;
    // ffmpeg stitches or replays the samples for other lists, and such rare lists are left to ffprobe.
    private static bool TryReadEdit(
        ReadOnlyMemory<byte>? edts,
        uint movieTimescale,
        uint timescale,
        out long emptyDuration,
        out long mediaTime,
        out long? editDuration)
    {
        emptyDuration = 0;
        mediaTime = 0;
        editDuration = null;
        if (edts is null || Find(ReadBoxes(edts.Value), _elst) is not { } elst)
        {
            return true;
        }

        var span = elst.Span;
        if (span.Length < 8)
        {
            throw new FormatException("The elst box is truncated.");
        }

        var isLong = span[0] == 1;
        var entrySize = isLong ? 20 : 12;
        var count = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
        if (count > (uint)((span.Length - 8) / entrySize))
        {
            throw new FormatException("The elst box is truncated.");
        }

        var hasMedia = false;
        for (var i = 0; i < (int)count; i++)
        {
            var entry = span.Slice(8 + (i * entrySize), entrySize);
            var segmentDuration = isLong ? BinaryPrimitives.ReadUInt64BigEndian(entry) : BinaryPrimitives.ReadUInt32BigEndian(entry);
            var time = isLong ? BinaryPrimitives.ReadInt64BigEndian(entry[8..]) : BinaryPrimitives.ReadInt32BigEndian(entry[4..]);
            // A 16.16 fixed-point number.
            var rate = BinaryPrimitives.ReadInt32BigEndian(entry[(isLong ? 16 : 8)..]);
            if (hasMedia)
            {
                return false;
            }

            // ffmpeg converts each duration to the media timescale.
            var duration = Rescale((long)Math.Min(segmentDuration, (ulong)long.MaxValue), timescale, movieTimescale);
            if (time == -1)
            {
                emptyDuration = checked(emptyDuration + duration);
                continue;
            }

            if (time < 0 || rate != 0x10000)
            {
                return false;
            }

            hasMedia = true;
            mediaTime = time;

            // ffmpeg drops the duration of an edit that ends past the largest timestamp.
            editDuration = duration > long.MaxValue - time ? 0 : duration;
        }

        return hasMedia;
    }

    // Walks the samples in decode order. Returns the presentation time of the first sample in the media timescale, or null without
    // samples, and adds the presentation time of every sync sample that is not empty to keyframes. editOrigin is the media time
    // ffmpeg presents first when it moves an edited video to the start of its edit: the later of the decode time of the first
    // sample presented in [editStart, editEnd) and the earliest presentation time there, or null when no sample is presented
    // there. sampleDuration is the sum of the sample durations.
    private static long? ReadSamples(
        ReadOnlySpan<byte> stts,
        ReadOnlySpan<byte> ctts,
        ReadOnlyMemory<byte>? stss,
        ReadOnlySpan<byte> stsz,
        long editStart,
        long editEnd,
        List<long>? keyframes,
        out long? editOrigin,
        out long sampleDuration)
    {
        var timeCount = ReadEntryCount(stts, 8);
        var offsetCount = ctts.IsEmpty ? 0 : ReadEntryCount(ctts, 8);
        var syncSpan = stss.HasValue ? stss.Value.Span : default;
        var syncCount = stss.HasValue ? ReadEntryCount(syncSpan, 4) : 0;

        long? firstPresentation = null;
        long? editDecode = null;
        long? editPresentation = null;
        long decodeTime = 0;
        long sampleNumber = 0;
        var offsetIndex = 0;
        long offsetRemaining = offsetCount > 0 ? BinaryPrimitives.ReadUInt32BigEndian(ctts[8..]) : 0;
        var syncIndex = 0;
        for (var i = 0; i < timeCount; i++)
        {
            var count = BinaryPrimitives.ReadUInt32BigEndian(stts[(8 + (i * 8))..]);
            var delta = BinaryPrimitives.ReadUInt32BigEndian(stts[(12 + (i * 8))..]);
            if (sampleNumber + count > MaxSamples)
            {
                throw new FormatException("The stts box lists too many samples.");
            }

            for (uint j = 0; j < count; j++)
            {
                sampleNumber++;
                long offset = 0;
                while (offsetRemaining == 0 && offsetIndex + 1 < offsetCount)
                {
                    offsetIndex++;
                    offsetRemaining = BinaryPrimitives.ReadUInt32BigEndian(ctts[(8 + (offsetIndex * 8))..]);
                }

                if (offsetRemaining > 0)
                {
                    // ffmpeg reads the offset as signed in both versions of the box.
                    offset = BinaryPrimitives.ReadInt32BigEndian(ctts[(12 + (offsetIndex * 8))..]);
                    offsetRemaining--;
                }

                var presentation = decodeTime + offset;
                firstPresentation ??= presentation;
                if (presentation >= editStart && presentation < editEnd)
                {
                    editDecode ??= decodeTime;
                    editPresentation = Math.Min(editPresentation ?? long.MaxValue, presentation);
                }

                if (keyframes is not null)
                {
                    // Without an stss box every sample is a sync sample.
                    var isSync = !stss.HasValue;
                    while (!isSync && syncIndex < syncCount)
                    {
                        var syncSample = BinaryPrimitives.ReadUInt32BigEndian(syncSpan[(8 + (syncIndex * 4))..]);
                        if (syncSample > sampleNumber)
                        {
                            break;
                        }

                        syncIndex++;
                        isSync = syncSample == sampleNumber;
                    }

                    if (isSync && !IsEmptySample(stsz, sampleNumber))
                    {
                        keyframes.Add(presentation);
                    }
                }

                decodeTime += delta;
            }
        }

        editOrigin = editDecode.HasValue && editPresentation.HasValue ? Math.Max(editDecode.Value, editPresentation.Value) : null;
        sampleDuration = decodeTime;
        return firstPresentation;
    }

    // Whether a sample, counted from 1, holds no data, so ffmpeg reads no packet for it. The stsz box holds the version and
    // flags, the size of every sample or 0, the sample count and, for 0, the size of each sample; without the box nothing is empty.
    private static bool IsEmptySample(ReadOnlySpan<byte> stsz, long sampleNumber)
    {
        if (stsz.IsEmpty)
        {
            return false;
        }

        if (stsz.Length < 12)
        {
            throw new FormatException("The stsz box is truncated.");
        }

        if (BinaryPrimitives.ReadUInt32BigEndian(stsz[4..]) != 0)
        {
            return false;
        }

        var offset = 12 + ((sampleNumber - 1) * 4);
        if (offset + 4 > stsz.Length)
        {
            throw new FormatException("The stsz box is truncated.");
        }

        return BinaryPrimitives.ReadUInt32BigEndian(stsz[(int)offset..]) == 0;
    }

    private static int ReadEntryCount(ReadOnlySpan<byte> box, int entrySize)
    {
        if (box.Length < 8)
        {
            throw new FormatException("A sample table box is truncated.");
        }

        var count = BinaryPrimitives.ReadUInt32BigEndian(box[4..]);
        if (count > (uint)((box.Length - 8) / entrySize))
        {
            throw new FormatException("A sample table box is truncated.");
        }

        return (int)count;
    }

    private static long ToTicks(long value, uint timescale) => Rescale(value, TimeSpan.TicksPerSecond, timescale);

    // Rounds to the nearest, as ffmpeg's av_rescale does.
    private static long Rescale(long value, long timescale, long fromTimescale)
        => (long)Math.Round((decimal)value * timescale / fromTimescale, MidpointRounding.AwayFromZero);

    // IsMedia is false for subtitle, chapter, timecode and other tracks that are neither audio nor video.
    private readonly record struct Track(bool IsUnsupported, bool IsMedia, long EndTicks, long? StartTicks, List<long>? Keyframes)
    {
        public static Track Unsupported => new(true, false, 0, null, null);
    }
}
