using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Keyframes;
using Jellyfin.MediaEncoding.Keyframes.FfProbe;
using Jellyfin.MediaEncoding.Keyframes.Matroska;
using Jellyfin.MediaEncoding.Keyframes.Mp4;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Ffmpeg;

/// <summary>
/// Every keyframe source gives times on the plan timeline: the source pts minus the format start time, the timeline of
/// ffmpeg <c>-ss</c> and <c>-copyts -start_at_zero</c>; and the container readers give the duration ffprobe gives the file.
/// </summary>
public sealed class KeyframeTimelineTests : IClassFixture<FfmpegFixture>
{
    // Container timestamps are rounded to milliseconds.
    private const double Tolerance = 0.001;

    // ffprobe adds up the start and the duration of the latest stream; an MP4 movie duration is rounded to the movie timescale,
    // milliseconds in the files ffmpeg writes.
    private const double DurationTolerance = 0.002;

    // The first cue starts with the file.
    private const string Subtitles = "1\n00:00:00,000 --> 00:00:01,000\nfirst\n\n2\n00:00:05,000 --> 00:00:06,000\nsecond\n";

    private readonly FfmpegFixture _ffmpeg;

    public KeyframeTimelineTests(FfmpegFixture ffmpeg)
    {
        _ffmpeg = ffmpeg;
    }

    [Theory]
    [InlineData("mkv")]
    [InlineData("late-video-mkv")]
    [InlineData("mp4")]
    [InlineData("late-video-mp4")]
    [InlineData("late-audio-mp4")]
    [InlineData("late-mp4")]
    [InlineData("edit-at-zero-mp4")]
    [InlineData("subtitle-mp4")]
    [InlineData("no-edit-list-mp4")]
    [InlineData("video-no-edit-list-mp4")]
    [InlineData("ts")]
    [InlineData("offset-ts")]
    [InlineData("two-video-ts")]
    public async Task Extractors_Keyframes_AreRelativeToTheFormatStart(string sample)
    {
        _ffmpeg.SkipIfUnavailable();
        var cancellationToken = TestContext.Current.CancellationToken;
        var path = await GetSampleAsync(sample, cancellationToken);
        var expected = await _ffmpeg.GetKeyframesAsync(path, cancellationToken);

        AssertKeyframes(expected, FfProbeKeyframeExtractor.GetKeyframeData(_ffmpeg.FfprobePath, path));
        if (path.EndsWith(".mkv", StringComparison.Ordinal))
        {
            AssertKeyframes(expected, MatroskaKeyframeExtractor.GetKeyframeData(path), await _ffmpeg.GetFormatDurationAsync(path, cancellationToken));
        }
        else if (path.EndsWith(".mp4", StringComparison.Ordinal))
        {
            AssertKeyframes(expected, Mp4KeyframeExtractor.GetKeyframeData(path), await _ffmpeg.GetFormatDurationAsync(path, cancellationToken));
        }
    }

    [Fact]
    public async Task Mp4Extractor_FragmentedFile_LeavesItToFfprobe()
    {
        _ffmpeg.SkipIfUnavailable();
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = _ffmpeg.CreateRunDirectory("timeline-fragmented-mp4");
        var path = Path.Combine(directory, "sample.mp4");

        // Without empty_moov the first fragment stays in the moov box.
        await _ffmpeg.FfmpegAsync(directory, ["-i", _ffmpeg.Mkv, "-c", "copy", "-movflags", "frag_keyframe", path], cancellationToken);

        Assert.Empty(Mp4KeyframeExtractor.GetKeyframeData(path).KeyframeTicks);
    }

    [Fact]
    public async Task Mp4Extractor_SubtitleBeforeTheMedia_LeavesItToFfprobe()
    {
        _ffmpeg.SkipIfUnavailable();
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = _ffmpeg.CreateRunDirectory("timeline-subtitle-first-mp4");
        var path = Path.Combine(directory, "sample.mp4");
        await File.WriteAllTextAsync(Path.Combine(directory, "subtitle.srt"), Subtitles, cancellationToken);

        // ffmpeg starts the format at the subtitle, half a second before the audio and video.
        await _ffmpeg.FfmpegAsync(
            directory,
            ["-itsoffset", "0.5", "-i", _ffmpeg.Mkv, "-i", "subtitle.srt", "-map", "0:v", "-map", "0:a", "-map", "1", "-c", "copy", "-c:s", "mov_text", path],
            cancellationToken);

        Assert.Empty(Mp4KeyframeExtractor.GetKeyframeData(path).KeyframeTicks);
    }

    private static void AssertKeyframes(IReadOnlyList<double> expected, KeyframeData actual, double expectedDuration)
    {
        AssertKeyframes(expected, actual);
        Assert.Equal(expectedDuration, (double)actual.TotalDuration / TimeSpan.TicksPerSecond, DurationTolerance);
    }

    private static void AssertKeyframes(IReadOnlyList<double> expected, KeyframeData actual)
    {
        Assert.Equal(expected.Count, actual.KeyframeTicks.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.Equal(expected[i], (double)actual.KeyframeTicks[i] / TimeSpan.TicksPerSecond, Tolerance);
        }
    }

    private async Task<string> GetSampleAsync(string sample, CancellationToken cancellationToken)
    {
        switch (sample)
        {
            case "mkv":
                return _ffmpeg.Mkv;
            case "late-video-mkv":
                return _ffmpeg.LateVideoMkv;
            case "mp4":
                return _ffmpeg.Mp4;
            case "ts":
                return _ffmpeg.Ts;
        }

        // The video starts 0.3 s late through an empty edit; the audio starts 0.5 s late; every track starts 8 s late; the video
        // edit is moved below to start at media time 0 despite the B-frame delay, with the movie box first so that it is found
        // before the media data; the subtitle track starts with the media; no edit list leaves the B-frame delay in the video
        // timestamps, and without audio the video starts the format; MPEG-TS starts at an arbitrary timestamp; the second video
        // stream has keyframes 0.3 s later than the first, whose keyframes alone count.
        string[] arguments = sample switch
        {
            "late-video-mp4" => ["-i", _ffmpeg.LateVideoMkv, "-c", "copy"],
            "late-audio-mp4" => ["-i", _ffmpeg.Mkv, "-itsoffset", "0.5", "-i", _ffmpeg.Mkv, "-map", "0:v:0", "-map", "1:a:0", "-c", "copy"],
            "late-mp4" => ["-itsoffset", "8", "-i", _ffmpeg.Mkv, "-c", "copy"],
            "edit-at-zero-mp4" => ["-i", _ffmpeg.Mkv, "-c", "copy", "-movflags", "+faststart"],
            "subtitle-mp4" => ["-i", _ffmpeg.Mkv, "-i", "subtitle.srt", "-map", "0:v", "-map", "0:a", "-map", "1", "-c", "copy", "-c:s", "mov_text"],
            "no-edit-list-mp4" => ["-i", _ffmpeg.Mkv, "-c", "copy", "-use_editlist", "0"],
            "video-no-edit-list-mp4" => ["-i", _ffmpeg.Mkv, "-map", "0:v", "-c", "copy", "-use_editlist", "0"],
            "offset-ts" => ["-i", _ffmpeg.Mkv, "-c", "copy", "-copyts", "-output_ts_offset", "100"],
            "two-video-ts" => ["-i", _ffmpeg.Mkv, "-i", _ffmpeg.LateVideoMkv, "-map", "0:v", "-map", "1:v", "-map", "0:a", "-c", "copy"],
            _ => throw new ArgumentOutOfRangeException(nameof(sample), sample, null)
        };
        var directory = _ffmpeg.CreateRunDirectory("timeline-" + sample);
        await File.WriteAllTextAsync(Path.Combine(directory, "subtitle.srt"), Subtitles, cancellationToken);
        var path = Path.Combine(directory, sample.EndsWith("-ts", StringComparison.Ordinal) ? "sample.ts" : "sample.mp4");
        await _ffmpeg.FfmpegAsync(directory, [.. arguments, path], cancellationToken);
        if (sample == "edit-at-zero-mp4")
        {
            await MoveTheVideoEditToZeroAsync(path, cancellationToken);
        }

        return path;
    }

    // The first edit list is that of the video, the first track: version 0 with one entry of a duration, a media time and a rate.
    private static async Task MoveTheVideoEditToZeroAsync(string path, CancellationToken cancellationToken)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        var elst = bytes.AsSpan().IndexOf("elst"u8);
        Assert.True(elst > 0);
        Assert.Equal(0, bytes[elst + 4]);
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(elst + 8)));
        Assert.True(BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(elst + 16)) > 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(elst + 16), 0);
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
    }
}
