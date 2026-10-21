using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Ffmpeg;

/// <summary>
/// The cuts of live HLS. The segment muxer opens a file at every keyframe (<c>-segment_time 0.001</c>) and writes each file's
/// start to a CSV list; a plan segment is served as the files of one run whose starts fall in its range. These tests pin the
/// ffmpeg behaviour that makes those files the same in every run, whichever segment a run starts from.
/// </summary>
public sealed class LiveSegmentCutTests : IClassFixture<FfmpegFixture>
{
    private const double SegmentLength = 6;

    // The CSV list prints microseconds; container timestamps are rounded to milliseconds.
    private const double Tolerance = 0.002;

    // An AAC frame at 48 kHz.
    private const double AudioPacket = 1024d / 48000;

    // An edit list would shift tfdt by the composition offset of the run's first packet, which millisecond sources round
    // differently from run to run; without one, and without shifting, tfdt stays on the timeline. The offset keeps the first
    // B-frame delay above zero, like the 2 * max_delay that MPEG-TS adds. empty_moov keeps samples out of the init segment.
    private const string FragmentOptions = "movflags=+frag_custom+empty_moov+frag_discont+skip_sidx+default_base_moof:use_editlist=0:avoid_negative_ts=disabled:output_ts_offset=10";

    private readonly FfmpegFixture _ffmpeg;

    public LiveSegmentCutTests(FfmpegFixture ffmpeg)
    {
        _ffmpeg = ffmpeg;
    }

    [Theory]
    [InlineData("mkv", false)]
    [InlineData("mkv", true)]
    [InlineData("mp4", false)]
    [InlineData("mp4", true)]
    [InlineData("ts", false)]
    [InlineData("ts", true)]
    public async Task Copy_FullRun_OpensAFileAtEveryKeyframe(string container, bool fmp4)
    {
        _ffmpeg.SkipIfUnavailable();
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = _ffmpeg.GetSource(container);
        var keyframes = await _ffmpeg.GetKeyframesAsync(source, cancellationToken);

        var entries = SegmentListEntry.Read(await CopyAsync($"keyframes-{container}-{fmp4}", source, null, fmp4, cancellationToken));

        // The first entry of a run has no start; every other one starts at a keyframe and ends where the next one starts.
        Assert.Equal(keyframes.Count, entries.Count);
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.Equal(keyframes[i], entries[i].Start, Tolerance);
            Assert.Equal(entries[i].Start, entries[i - 1].End, Tolerance);
        }
    }

    [Theory]
    [InlineData("mkv", false)]
    [InlineData("mkv", true)]
    [InlineData("mp4", false)]
    [InlineData("mp4", true)]
    [InlineData("ts", false)]
    [InlineData("ts", true)]
    public async Task Copy_RestartBeforeThePrecedingKeyframe_ReproducesTheFilesFromTheBoundary(string container, bool fmp4)
    {
        _ffmpeg.SkipIfUnavailable();
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = _ffmpeg.GetSource(container);
        var keyframes = await _ffmpeg.GetKeyframesAsync(source, cancellationToken);
        var full = await CopyAsync($"restart-{container}-{fmp4}", source, null, fmp4, cancellationToken);
        var fullEntries = SegmentListEntry.Read(full);

        foreach (var boundary in KeyframePlan(keyframes).Skip(1))
        {
            var run = await RestartCopyAsync($"restart-{container}-{fmp4}-{FfmpegFixture.FormatSeconds(boundary)}", source, keyframes, boundary, fmp4, cancellationToken);
            var entries = SegmentListEntry.Read(run);
            var first = FindStart(entries, boundary);
            Assert.True(first > 0, $"No file after the first one starts at {boundary}");

            // The same packets and timestamps as in the full run, for the boundary's file and the one after it.
            for (var i = first; i < Math.Min(first + 2, entries.Count); i++)
            {
                var expected = fullEntries[FindStart(fullEntries, entries[i].Start)];
                AssertSamePackets(
                    await GetPacketsAsync(full, [expected.FileName], fmp4, cancellationToken),
                    await GetPacketsAsync(run, [entries[i].FileName], fmp4, cancellationToken));
            }
        }
    }

    [Theory]
    [InlineData("mkv", false)]
    [InlineData("mkv", true)]
    [InlineData("mp4", false)]
    [InlineData("mp4", true)]
    [InlineData("ts", false)]
    [InlineData("ts", true)]
    public async Task Copy_SegmentsFromTwoRuns_PlayAsOneStream(string container, bool fmp4)
    {
        _ffmpeg.SkipIfUnavailable();
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = _ffmpeg.GetSource(container);
        var keyframes = await _ffmpeg.GetKeyframesAsync(source, cancellationToken);
        var plan = KeyframePlan(keyframes);
        var full = await CopyAsync($"junction-{container}-{fmp4}", source, null, fmp4, cancellationToken);
        var run = await RestartCopyAsync($"junction-{container}-{fmp4}-3", source, keyframes, plan[3], fmp4, cancellationToken);

        // Segment 2 from the full run, then segment 3 from the run restarted for it, behind the full run's init segment:
        // what a player gets when it seeks past the encode and later plays across the junction.
        var init = fmp4 ? Path.Combine(full, "init.mp4") : null;
        var fullEntries = SegmentListEntry.Read(full);
        var mixed = FilesOf(fullEntries, plan[2], plan[3]).Select(f => Path.Combine(full, f))
            .Concat(FilesOf(SegmentListEntry.Read(run), plan[3], plan[4]).Select(f => Path.Combine(run, f)))
            .ToList();
        var single = FilesOf(fullEntries, plan[2], plan[4]).Select(f => Path.Combine(full, f)).ToList();

        Assert.Empty(await _ffmpeg.GetDecodeErrorsAsync(init, mixed, cancellationToken));
        AssertSamePackets(
            await _ffmpeg.GetSegmentPacketsAsync(init, single, cancellationToken),
            await _ffmpeg.GetSegmentPacketsAsync(init, mixed, cancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transcode_ForcedKeyframeList_CutsAtTheSameFramesInEveryRun(bool lateVideo)
    {
        _ffmpeg.SkipIfUnavailable();
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = lateVideo ? _ffmpeg.LateVideoMkv : _ffmpeg.Mkv;
        const double HalfFrame = FfmpegFixture.FrameDuration / 2;

        // A uniform plan whole in frames: 144 frames, 6.006 s.
        var length = Math.Ceiling(SegmentLength / FfmpegFixture.FrameDuration) * FfmpegFixture.FrameDuration;
        var plan = Enumerable.Range(0, (int)Math.Ceiling(FfmpegFixture.SourceSeconds / length)).Select(k => k * length).ToList();
        var full = SegmentListEntry.Read(await TranscodeAsync($"transcode-{lateVideo}", source, plan, 0, cancellationToken));

        // One file per plan segment, so no keyframe but the forced ones, each at the frame closest to its boundary.
        Assert.Equal(plan.Count, full.Count);
        for (var k = 1; k < plan.Count; k++)
        {
            Assert.InRange(full[k].Start, plan[k] - HalfFrame, plan[k] + HalfFrame);
        }

        foreach (var n in new[] { 2, 5 })
        {
            // The first file of a restart is segment n itself; the others start where the full run's do.
            var entries = SegmentListEntry.Read(await TranscodeAsync($"transcode-{lateVideo}-{n}", source, plan, n, cancellationToken));
            Assert.Equal(plan.Count - n, entries.Count);
            for (var i = 1; i < entries.Count; i++)
            {
                Assert.Equal(full[n + i].Start, entries[i].Start, Tolerance);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AudioOnly_SegmentTime_CutsWithinAPacketOfThePlanInEveryRun(bool copy)
    {
        _ffmpeg.SkipIfUnavailable();
        var cancellationToken = TestContext.Current.CancellationToken;

        // Every audio packet is a keyframe, so the files are cut by time: at the first packet k segment lengths after the run's
        // first packet. A restart starts within a packet of its boundary, so its cuts stay within two packets of the plan, but
        // may differ from the full run's by a packet or two at a junction.
        var full = await AudioAsync($"audio-{copy}", 0, copy, cancellationToken);
        var fullEntries = SegmentListEntry.Read(full);
        var segments = (int)Math.Ceiling(FfmpegFixture.SourceSeconds / SegmentLength);
        Assert.InRange(fullEntries.Count, segments, segments + 1);

        // MPEG-TS shifts the timeline by a constant.
        var offset = await GetFirstPtsAsync(full, fullEntries[1], cancellationToken) - fullEntries[1].Start;
        AssertCutsFollowFirstPacket(fullEntries, await GetFirstPtsAsync(full, fullEntries[0], cancellationToken) - offset);
        foreach (var n in new[] { 2, 4 })
        {
            var run = await AudioAsync($"audio-{copy}-{n}", n * SegmentLength, copy, cancellationToken);
            var entries = SegmentListEntry.Read(run);
            var first = await GetFirstPtsAsync(run, entries[0], cancellationToken) - offset;
            Assert.InRange(first, (n * SegmentLength) - AudioPacket - Tolerance, (n * SegmentLength) + AudioPacket + Tolerance);
            AssertCutsFollowFirstPacket(entries, first);
        }
    }

    private static void AssertCutsFollowFirstPacket(IReadOnlyList<SegmentListEntry> entries, double first)
    {
        for (var i = 1; i < entries.Count; i++)
        {
            Assert.InRange(entries[i].Start, first + (i * SegmentLength) - Tolerance, first + (i * SegmentLength) + AudioPacket + Tolerance);
        }
    }

    // Video timestamps pass through a copy unchanged. Copied audio timestamps are rebuilt from sample counts starting at the
    // run's first packet, so a source with millisecond timestamps rounds them differently in each run, by less than 1 ms.
    private static void AssertSamePackets(IReadOnlyList<ProbePacket> expected, IReadOnlyList<ProbePacket> actual)
    {
        Assert.Equal(expected.Where(p => p.StreamIndex == 0), actual.Where(p => p.StreamIndex == 0));
        var expectedAudio = expected.Where(p => p.StreamIndex != 0).ToList();
        var actualAudio = actual.Where(p => p.StreamIndex != 0).ToList();
        Assert.Equal(expectedAudio.Select(p => p.Size), actualAudio.Select(p => p.Size));
        for (var i = 0; i < expectedAudio.Count; i++)
        {
            Assert.Equal(FfmpegFixture.ParseSeconds(expectedAudio[i].Pts), FfmpegFixture.ParseSeconds(actualAudio[i].Pts), 0.001);
        }
    }

    // The rule of DynamicHlsPlaylistGenerator.ComputeSegments: cut at the first keyframe at or after k segment lengths.
    private static List<double> KeyframePlan(IReadOnlyList<double> keyframes)
    {
        var plan = new List<double> { 0 };
        var next = SegmentLength;
        foreach (var keyframe in keyframes)
        {
            if (keyframe >= next)
            {
                plan.Add(keyframe);
                next += SegmentLength;
            }
        }

        return plan;
    }

    // The entry after the first whose start is the given time, or -1.
    private static int FindStart(IReadOnlyList<SegmentListEntry> entries, double start)
    {
        for (var i = 1; i < entries.Count; i++)
        {
            if (Math.Abs(entries[i].Start - start) < Tolerance)
            {
                return i;
            }
        }

        return -1;
    }

    // The files of a plan segment: those after a run's first whose start falls in the segment.
    private static IEnumerable<string> FilesOf(IReadOnlyList<SegmentListEntry> entries, double start, double end)
        => entries.Skip(1).Where(e => e.Start >= start - Tolerance && e.Start < end - Tolerance).Select(e => e.FileName);

    // Settings that keep the timeline the same in every run: -copyts -start_at_zero, no shifting by either muxer, and a
    // constant MPEG-TS offset from -max_delay.
    private static List<string> SegmentOutput(bool fmp4, string segmentTime)
    {
        List<string> arguments =
        [
            "-copyts", "-start_at_zero", "-avoid_negative_ts", "disabled", "-f", "segment", "-max_delay", "5000000",
            "-segment_time", segmentTime, "-segment_list", "list.csv", "-segment_list_type", "csv", "-individual_header_trailer", "0"
        ];
        arguments.AddRange(fmp4
            ? ["-segment_format", "mp4", "-segment_header_filename", "init.mp4", "-segment_format_options", FragmentOptions, "g%d.m4s"]
            : ["-segment_format", "mpegts", "-segment_format_options", "avoid_negative_ts=disabled", "g%d.ts"]);
        return arguments;
    }

    private async Task<string> CopyAsync(string name, string source, double? seek, bool fmp4, CancellationToken cancellationToken)
    {
        var directory = _ffmpeg.CreateRunDirectory(name);
        List<string> arguments = [];
        if (seek.HasValue)
        {
            arguments.AddRange(["-ss", FfmpegFixture.FormatSeconds(seek.Value)]);
        }

        arguments.AddRange(["-i", source, "-map", "0:v:0", "-map", "0:a:0", "-codec:v:0", "copy", "-codec:a:0", "copy"]);
        if (fmp4 && source.EndsWith(".ts", StringComparison.Ordinal))
        {
            arguments.AddRange(["-bsf:a", "aac_adtstoasc"]);
        }

        arguments.AddRange(SegmentOutput(fmp4, "0.001"));
        await _ffmpeg.FfmpegAsync(directory, arguments, cancellationToken);
        return directory;
    }

    // MPEG-TS has no index: a seek lands on any packet and the copy starts at the next keyframe. Seeking a second before
    // the keyframe preceding the boundary makes that keyframe, or an earlier one, open the run, so the boundary opens a later
    // file, whose start the list records. Indexed containers land on the last keyframe before the seek point instead.
    private Task<string> RestartCopyAsync(string name, string source, IReadOnlyList<double> keyframes, double boundary, bool fmp4, CancellationToken cancellationToken)
    {
        var seek = keyframes.Last(k => k < boundary - Tolerance) - 1;
        return CopyAsync(name, source, seek > 0 ? seek : null, fmp4, cancellationToken);
    }

    private async Task<string> TranscodeAsync(string name, string source, IReadOnlyList<double> plan, int start, CancellationToken cancellationToken)
    {
        const double HalfFrame = FfmpegFixture.FrameDuration / 2;
        var directory = _ffmpeg.CreateRunDirectory(name);
        List<string> arguments = [];
        if (start > 0)
        {
            arguments.AddRange(["-ss", FfmpegFixture.FormatSeconds(plan[start] - HalfFrame)]);
        }

        // Absolute times: in an expression t counts from the run's first frame, which is off the plan grid when the video
        // starts late, and a restart would then force other frames than the full run.
        var keyframes = string.Join(',', plan.Skip(Math.Max(start, 1)).Select(b => FfmpegFixture.FormatSeconds(b - HalfFrame)));
        arguments.AddRange(
        [
            "-i", source, "-map", "0:v:0", "-map", "0:a:0",
            "-codec:v:0", "libx264", "-preset", "veryfast", "-bf", "2", "-sc_threshold:v:0", "0", "-g:v:0", "432",
            "-force_key_frames:0", keyframes, "-codec:a:0", "aac", "-b:a", "96k"
        ]);
        arguments.AddRange(SegmentOutput(false, "0.001"));
        await _ffmpeg.FfmpegAsync(directory, arguments, cancellationToken);
        return directory;
    }

    private async Task<string> AudioAsync(string name, double start, bool copy, CancellationToken cancellationToken)
    {
        var directory = _ffmpeg.CreateRunDirectory(name);
        var at = FfmpegFixture.FormatSeconds(start);
        List<string> arguments = [];
        if (copy)
        {
            // A copy starts where the seek lands; trimming at the output starts it at the boundary, and the offset undoes the
            // shift that the output seek applies to the timestamps.
            if (start > 0)
            {
                arguments.AddRange(["-ss", FfmpegFixture.FormatSeconds(start - 2)]);
            }

            arguments.AddRange(["-i", _ffmpeg.Mkv, "-map", "0:a:0", "-codec:a:0", "copy"]);
            if (start > 0)
            {
                arguments.AddRange(["-ss", at, "-output_ts_offset", at]);
            }
        }
        else
        {
            // Transcoding trims the decoded audio at the seek point.
            if (start > 0)
            {
                arguments.AddRange(["-ss", at]);
            }

            arguments.AddRange(["-i", _ffmpeg.Mkv, "-map", "0:a:0", "-codec:a:0", "aac", "-b:a", "96k"]);
        }

        arguments.AddRange(SegmentOutput(false, FfmpegFixture.FormatSeconds(SegmentLength)));
        await _ffmpeg.FfmpegAsync(directory, arguments, cancellationToken);
        return directory;
    }

    private Task<IReadOnlyList<ProbePacket>> GetPacketsAsync(string directory, IEnumerable<string> files, bool fmp4, CancellationToken cancellationToken)
        => _ffmpeg.GetSegmentPacketsAsync(fmp4 ? Path.Combine(directory, "init.mp4") : null, files.Select(f => Path.Combine(directory, f)), cancellationToken);

    private async Task<double> GetFirstPtsAsync(string directory, SegmentListEntry entry, CancellationToken cancellationToken)
    {
        var packets = await _ffmpeg.GetPacketsAsync(Path.Combine(directory, entry.FileName), null, cancellationToken);
        return FfmpegFixture.ParseSeconds(packets[0].Pts);
    }
}
