using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Jellyfin.MediaEncoding.Keyframes.Mp4;

public class Mp4KeyframeExtractorTests
{
    // 23.976 fps in the 24000 timescale ffmpeg writes, and a two-frame B-frame delay.
    private const uint VideoTimescale = 24000;
    private const uint FrameDelta = 1001;
    private const int Delay = 2002;

    private const uint AudioTimescale = 48000;
    private const uint AudioDelta = 1024;

    [Fact]
    public void GetKeyframeData_BFramesWithEditList_StartsAtZero()
    {
        // ffmpeg writes an edit that skips the B-frame delay of the video and the priming samples of the audio.
        var file = Mp4File(
            Video(stss: [1, 6], edits: [(1000, Delay)]),
            Audio(edits: [(1000, (int)AudioDelta)]));

        var result = Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file));

        Assert.Equal([0, Ticks(5 * FrameDelta, VideoTimescale)], result.KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_TotalDuration_LastsToTheEndOfTheLatestTrack()
    {
        // The video ends after the audio through its 3 s empty edit; ffprobe ignores the movie duration.
        var file = Mp4File(1300, Video(stss: [1, 6], edits: [(3000, -1), (1000, Delay)]), Audio(edits: null));

        Assert.Equal(30_000_000 + Ticks(10 * FrameDelta, VideoTimescale), Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).TotalDuration);
    }

    [Fact]
    public void GetKeyframeData_TotalDuration_StartsAtTheFormatStart()
    {
        // Both tracks start 3 s late; the audio ends last.
        var file = Mp4File(
            Video(stss: [1, 6], edits: [(3000, -1), (1000, Delay)]),
            Audio(edits: [(3000, -1), (3000, 0)]));

        Assert.Equal(Ticks(100 * AudioDelta, AudioTimescale), Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).TotalDuration);
    }

    [Fact]
    public void GetKeyframeData_TotalDuration_EndsWithTheEditsAndTheEmptyOnes()
    {
        // ffmpeg caps a stream at the sum of its edits, the empty ones included: from 1 s, the audio lasts 1.5 s, not 0.5 s.
        var file = Mp4File(
            Video(stss: [1, 6], edits: [(1000, -1), (1000, Delay)]),
            Audio(edits: [(1000, -1), (500, 0)]));

        Assert.Equal(15_000_000, Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).TotalDuration);
    }

    [Fact]
    public void GetKeyframeData_TotalDurationWithoutEditList_EndsAfterTheCompositionOffset()
    {
        // The video starts and ends two frames late; it starts the format, so the duration is its own.
        var file = Mp4File(Video(stss: [1, 6], edits: null));

        Assert.Equal(Ticks(10 * FrameDelta, VideoTimescale), Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).TotalDuration);
    }

    [Fact]
    public void GetKeyframeData_TotalDuration_EndsWithTheSamples()
    {
        // The media header lasts 20 frames, the samples 10, as after a stream copy that ends within a GOP.
        var file = Mp4File(Track("vide", VideoTimescale, 20 * FrameDelta, [FullBox("stts", UInt32(1, 10, FrameDelta)), FullBox("stss", UInt32(1, 1))], null));

        Assert.Equal(Ticks(10 * FrameDelta, VideoTimescale), Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).TotalDuration);
    }

    [Fact]
    public void GetKeyframeData_TotalDuration_EndsWithTheMediaEdit()
    {
        // The edit keeps the first 0.5 s of the audio.
        var file = Mp4File(Video(stss: [1, 6], edits: [(1000, Delay)]), Audio(edits: [(500, 0)]));

        Assert.Equal(5_000_000, Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).TotalDuration);
    }

    [Fact]
    public void GetKeyframeData_EmptyEdit_DelaysTheVideo()
    {
        // The video starts 0.3 s after the audio, which starts the format.
        var file = Mp4File(
            Video(stss: [1, 6], edits: [(300, -1), (1000, Delay)]),
            Audio(edits: null));

        var result = Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file));

        Assert.Equal([3_000_000, 3_000_000 + Ticks(5 * FrameDelta, VideoTimescale)], result.KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_WithoutEditList_PresentsAfterTheCompositionOffset()
    {
        var file = Mp4File(Video(stss: [1, 6], edits: null), Audio(edits: null));

        var result = Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file));

        Assert.Equal([Ticks(Delay, VideoTimescale), Ticks((5 * FrameDelta) + Delay, VideoTimescale)], result.KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_EditAtTheDecodeStart_PresentsTheFirstFrameAtTheEditStart()
    {
        // The edit starts at media time 0 despite the B-frame delay; ffmpeg moves the earliest video frame in it to its start.
        var file = Mp4File(
            Video(stss: [1, 6], edits: [(300, -1), (1000, 0)]),
            Audio(edits: [(1000, 0)]));

        var result = Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file));

        Assert.Equal([3_000_000, 3_000_000 + Ticks(5 * FrameDelta, VideoTimescale)], result.KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_WithoutEditList_StartsAtTheFirstSample()
    {
        // The second frame is presented first, before the first frame.
        var offsets = FullBox("ctts", UInt32(3, 1, FrameDelta, 1, -FrameDelta, 8, FrameDelta));
        var file = Mp4File(Track("vide", VideoTimescale, 10 * FrameDelta, [FullBox("stts", UInt32(1, 10, FrameDelta)), offsets, FullBox("stss", UInt32(2, 1, 6))], null));

        var result = Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file));

        Assert.Equal([0, Ticks(5 * FrameDelta, VideoTimescale)], result.KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_NegativeOffsetsWithoutEditList_StartTheFormatBeforeZero()
    {
        // Every frame is presented two frames before its decode time, so the video starts the format at -2 frames.
        var file = Mp4File(Video(stss: [1, 6], edits: null, offset: -Delay), Audio(edits: null));

        var result = Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file));

        Assert.Equal([0, Ticks(5 * FrameDelta, VideoTimescale)], result.KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_EveryTrackDelayed_IsRelativeToTheFirstTrack()
    {
        // ffmpeg writes the delay of the audio less its priming samples as the empty edit: the audio starts at 0.479 s, the
        // video at 0.5 s.
        var file = Mp4File(
            Video(stss: [1, 6], edits: [(500, -1), (1000, Delay)]),
            Audio(edits: [(479, -1), (1000, 0)]));

        var result = Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file));

        Assert.Equal([210_000, 210_000 + Ticks(5 * FrameDelta, VideoTimescale)], result.KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_PrimingAfterAnEmptyEdit_StartsAtTheEndOfTheEmptyEdit()
    {
        // ffmpeg drops the priming samples before the media time; the audio starts with the video at 0.5 s.
        var file = Mp4File(
            Video(stss: [1, 6], edits: [(500, -1), (1000, Delay)]),
            Audio(edits: [(500, -1), (1000, (int)AudioDelta)]));

        var result = Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file));

        Assert.Equal([0, Ticks(5 * FrameDelta, VideoTimescale)], result.KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_WithoutSyncSampleTable_EverySampleIsAKeyframe()
    {
        var file = Mp4File(Video(stss: null, edits: null, offset: 0));

        var result = Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file));

        Assert.Equal(Enumerable.Range(0, 10).Select(i => Ticks((uint)i * FrameDelta, VideoTimescale)), result.KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_Fragmented_HasNoKeyframes()
    {
        // A fragmented file keeps its samples in moof boxes.
        var file = Mp4File(Video(stss: [], edits: null, samples: 0));

        Assert.Empty(Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_FragmentedWithSamplesInTheMovieBox_HasNoKeyframes()
    {
        // Written without empty_moov, a fragmented file keeps its first fragment in the moov box and the rest in moof boxes.
        var file = Mp4File(0, Video(stss: [1, 6], edits: null), Box("mvex", FullBox("trex", UInt32(1, 1, 0, 0, 0))));

        Assert.Empty(Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_TwoMediaEdits_IsLeftToFfprobe()
    {
        var file = Mp4File(Video(stss: [1, 6], edits: [(200, 0), (200, 5005)]));

        Assert.Empty(Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_PartialSyncSamples_AreLeftToFfprobe()
    {
        var file = Mp4File(Video(stss: [1], edits: null, sampleTableBox: FullBox("stps", UInt32(1, 6))));

        Assert.Empty(Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).KeyframeTicks);
    }

    [Theory]
    [InlineData("rap ")]
    [InlineData("sync")]
    public void GetKeyframeData_RandomAccessGroup_IsLeftToFfprobe(string groupingType)
    {
        // Every sample in group 1.
        var group = FullBox("sbgp", Encoding.ASCII.GetBytes(groupingType), UInt32(1, 10, 1));
        var file = Mp4File(Video(stss: [1], edits: null, sampleTableBox: group));

        Assert.Empty(Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_RollGroup_KeepsTheSyncSamples()
    {
        var group = FullBox("sbgp", Encoding.ASCII.GetBytes("roll"), UInt32(1, 10, 1));
        var file = Mp4File(Video(stss: [1, 6], edits: null, sampleTableBox: group));

        Assert.Equal(2, Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).KeyframeTicks.Count);
    }

    [Fact]
    public void GetKeyframeData_EditAtAnotherRate_IsLeftToFfprobe()
    {
        // 1.5: the integer part alone is 1.
        var file = Mp4File(Video(stss: [1, 6], edits: [(1000, Delay)], rate: 0x18000));

        Assert.Empty(Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_EmptySyncSample_IsNoKeyframe()
    {
        // ffmpeg reads no packet for the empty sixth sample.
        var sizes = FullBox("stsz", UInt32(0, 10, 9, 9, 9, 9, 9, 0, 9, 9, 9, 9));
        var file = Mp4File(Video(stss: [1, 6], edits: [(1000, Delay)], sampleTableBox: sizes));

        Assert.Equal([0], Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_CompactSampleSizes_AreLeftToFfprobe()
    {
        var sizes = FullBox("stz2", UInt32(8, 10), Enumerable.Repeat((byte)9, 10).ToArray());
        var file = Mp4File(Video(stss: [1, 6], edits: [(1000, Delay)], sampleTableBox: sizes));

        Assert.Empty(Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_EditListWithoutEntries_IsLeftToFfprobe()
    {
        var file = Mp4File(Video(stss: [1, 6], edits: []));

        Assert.Empty(Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_EmptyEditsAlone_AreLeftToFfprobe()
    {
        var file = Mp4File(Video(stss: [1, 6], edits: [(300, -1)]));

        Assert.Empty(Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_NegativeMediaTime_IsLeftToFfprobe()
    {
        var file = Mp4File(Video(stss: [1, 6], edits: [(1000, -2)]));

        Assert.Empty(Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_EmptyEditAfterTheMedia_IsLeftToFfprobe()
    {
        // ffmpeg can replay the samples for it.
        var file = Mp4File(Video(stss: [1, 6], edits: [(1000, Delay), (300, -1)]));

        Assert.Empty(Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).KeyframeTicks);
    }

    [Theory]
    [InlineData(100, Delay)] // Ends before the second keyframe.
    [InlineData(1000, Delay + (int)FrameDelta)] // Starts after the first keyframe.
    public void GetKeyframeData_EditCutsAKeyframe_IsLeftToFfprobe(int duration, int mediaTime)
    {
        var file = Mp4File(Video(stss: [1, 6], edits: [(duration, mediaTime)]));

        Assert.Empty(Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_SubtitleBeforeTheMedia_IsLeftToFfprobe()
    {
        var file = Mp4File(
            Video(stss: [1, 6], edits: [(500, -1), (1000, Delay)]),
            Audio(edits: [(500, -1), (1000, 0)]),
            Subtitle(edits: null));

        Assert.Empty(Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file)).KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_SubtitleAfterTheMediaStart_KeepsTheFormatStart()
    {
        var file = Mp4File(
            Video(stss: [1, 6], edits: [(300, -1), (1000, Delay)]),
            Audio(edits: null),
            Subtitle(edits: [(200, -1), (1000, 0)]));

        var result = Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(file));

        Assert.Equal([3_000_000, 3_000_000 + Ticks(5 * FrameDelta, VideoTimescale)], result.KeyframeTicks);
    }

    [Fact]
    public void GetKeyframeData_MovieBoxAfterLargeMediaData_IsFound()
    {
        var movie = Movie(0, Video(stss: [1, 6], edits: [(1000, Delay)]));

        // A 64-bit box size, as for media data over 4 GiB.
        var mediaData = new byte[16 + 100];
        BinaryPrimitives.WriteUInt32BigEndian(mediaData, 1);
        Encoding.ASCII.GetBytes("mdat").CopyTo(mediaData, 4);
        BinaryPrimitives.WriteUInt64BigEndian(mediaData.AsSpan(8), (ulong)mediaData.Length);

        var result = Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream([.. FileType(), .. mediaData, .. movie]));

        Assert.Equal(2, result.KeyframeTicks.Count);
    }

    [Fact]
    public void GetKeyframeData_WithoutMovieBox_Throws()
    {
        Assert.Throws<FormatException>(() => Mp4KeyframeExtractor.GetKeyframeData(new MemoryStream(FileType())));
    }

    private static long Ticks(long value, uint timescale) => (long)Math.Round((decimal)value * TimeSpan.TicksPerSecond / timescale, MidpointRounding.AwayFromZero);

    private static byte[] Mp4File(params byte[][] tracks) => Mp4File(0, tracks);

    private static byte[] Mp4File(long movieDuration, params byte[][] tracks) => [.. FileType(), .. Movie(movieDuration, tracks)];

    private static byte[] FileType() => Box("ftyp", Encoding.ASCII.GetBytes("isom"), UInt32(512), Encoding.ASCII.GetBytes("isomiso2avc1mp41"));

    // The movie timescale is 1000, so the movie duration and edit durations are milliseconds.
    private static byte[] Movie(long duration, params byte[][] boxes)
        => Box("moov", [FullBox("mvhd", UInt32(0, 0, 1000, duration), new byte[80]), .. boxes]);

    // Ten frames, keyframes as given; each frame is presented the offset after its decode time, two frames by default. The rate
    // of the edits is 16.16 fixed-point.
    private static byte[] Video(
        uint[]? stss,
        (int Duration, int MediaTime)[]? edits,
        int offset = Delay,
        uint samples = 10,
        byte[]? sampleTableBox = null,
        int rate = 0x10000)
    {
        List<byte[]> table = [FullBox("stts", samples == 0 ? UInt32(0) : UInt32(1, samples, FrameDelta))];
        if (offset != 0 && samples > 0)
        {
            table.Add(FullBox("ctts", UInt32(1, samples, offset)));
        }

        if (stss is not null)
        {
            table.Add(FullBox("stss", UInt32([stss.Length, .. stss.Select(s => (long)s)])));
        }

        if (sampleTableBox is not null)
        {
            table.Add(sampleTableBox);
        }

        return Track("vide", VideoTimescale, samples * FrameDelta, table, edits, rate);
    }

    private static byte[] Audio((int Duration, int MediaTime)[]? edits)
        => Track("soun", AudioTimescale, 100 * AudioDelta, [FullBox("stts", UInt32(1, 100, AudioDelta))], edits);

    // One cue of a second.
    private static byte[] Subtitle((int Duration, int MediaTime)[]? edits)
        => Track("sbtl", 1000, 1000, [FullBox("stts", UInt32(1, 1, 1000))], edits);

    private static byte[] Track(string handler, uint timescale, long duration, List<byte[]> sampleTable, (int Duration, int MediaTime)[]? edits, int rate = 0x10000)
    {
        var media = Box(
            "mdia",
            FullBox("mdhd", UInt32(0, 0, timescale, duration), new byte[4]),
            FullBox("hdlr", UInt32(0), Encoding.ASCII.GetBytes(handler), new byte[13]),
            Box("minf", Box("stbl", [.. sampleTable])));
        if (edits is null)
        {
            return Box("trak", media);
        }

        var list = new List<byte[]> { UInt32(edits.Length) };
        foreach (var (editDuration, mediaTime) in edits)
        {
            list.Add(UInt32(editDuration, (uint)mediaTime));
            list.Add(UInt32(rate));
        }

        return Box("trak", Box("edts", FullBox("elst", [.. list])), media);
    }

    private static byte[] FullBox(string type, params byte[][] parts) => Box(type, [[0, 0, 0, 0], .. parts]);

    private static byte[] Box(string type, params byte[][] parts)
    {
        var payload = parts.SelectMany(p => p).ToArray();
        var box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] UInt32(params long[] values)
    {
        var bytes = new byte[values.Length * 4];
        for (var i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(i * 4), unchecked((uint)values[i]));
        }

        return bytes;
    }
}
