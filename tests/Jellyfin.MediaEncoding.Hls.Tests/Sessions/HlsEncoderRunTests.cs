using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Plans;
using Jellyfin.MediaEncoding.Hls.Sessions;
using Jellyfin.MediaEncoding.Hls.Tests.Ffmpeg;
using Jellyfin.MediaEncoding.Keyframes;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Sessions;

/// <summary>
/// Runs that read the list and files of a real ffmpeg process, started with the segment muxer options of the controller.
/// </summary>
public sealed class HlsEncoderRunTests : IClassFixture<FfmpegFixture>
{
    private const double SegmentLength = 6;

    private const string FragmentOptions = "movflags=+frag_custom+empty_moov+frag_discont+skip_sidx+default_base_moof:use_editlist=0:avoid_negative_ts=disabled:output_ts_offset=10";

    private static readonly TimeSpan _timeout = TimeSpan.FromMinutes(1);

    private readonly FfmpegFixture _ffmpeg;

    public HlsEncoderRunTests(FfmpegFixture ffmpeg)
    {
        _ffmpeg = ffmpeg;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transcode_CappedRun_CompletesEachSegmentWhileWritingAndStopsAtItsEnd(bool fmp4)
    {
        _ffmpeg.SkipIfUnavailable();
        var cancellationToken = TestContext.Current.CancellationToken;
        var plan = SegmentPlanner.CreateUniformPlan(Ticks(FfmpegFixture.SourceSeconds), Ticks(SegmentLength), 1 / FfmpegFixture.FrameDuration);
        using var run = CreateRun($"run-transcode-{fmp4}", plan, 2, 5, HlsCutMode.TranscodeVideo, fmp4);
        using var job = CreateJob(run);

        // Read at six times the speed, so that the run is still writing when its first segment completes.
        var keyframes = string.Join(',', HlsCuts.GetForcedKeyframeTicks(plan, run.StartIndex, run.EndIndex).Select(HlsCuts.FormatSeconds));
        List<string> arguments =
        [
            "-readrate", "6", .. Seek(run), "-i", _ffmpeg.Mkv, "-map", "0:v:0", "-map", "0:a:0",
            "-codec:v:0", "libx264", "-preset", "veryfast", "-bf", "2", "-sc_threshold:v:0", "0", "-g:v:0", "432",
            "-force_key_frames:0", keyframes, "-codec:a:0", "aac", "-b:a", "96k",
            "-to", HlsCuts.FormatSeconds(plan.Segments[run.EndIndex].StartTicks - plan.ToleranceTicks)
        ];
        var exit = Start(run, job, arguments, fmp4, cancellationToken);

        if (fmp4)
        {
            Assert.True(await run.WaitForInitAsync(cancellationToken).WaitAsync(_timeout, cancellationToken));
        }

        // The first file of a transcode is its first segment, complete once the next file opens.
        Assert.Equal(new HlsSegmentLocation(HlsSegmentState.Complete, 0, 1), await run.WaitForAsync(2, cancellationToken).WaitAsync(_timeout, cancellationToken));
        Assert.False(job.HasExited);
        Assert.True(job.CompletedPositionTicks >= plan.Segments[2].EndTicks);

        Assert.Equal(new HlsSegmentLocation(HlsSegmentState.Complete, 1, 1), await run.WaitForAsync(3, cancellationToken).WaitAsync(_timeout, cancellationToken));

        // The last segment of the run completes when ffmpeg ends there, without a file after it.
        Assert.Equal(new HlsSegmentLocation(HlsSegmentState.Complete, 2, 1), await run.WaitForAsync(4, cancellationToken).WaitAsync(_timeout, cancellationToken));
        Assert.Empty(await exit);
        Assert.Equal(HlsRunState.Finished, run.State);

        var list = new HlsListReader(run.ListPath);
        list.Read();
        Assert.Equal(3, list.Entries.Count);
        Assert.Equal(plan.Segments[4].EndTicks, job.CompletedPositionTicks);

        var files = Enumerable.Range(2, 3).SelectMany(index => run.GetFiles(run.Locate(index))).ToList();
        Assert.Equal(list.Entries.Select(e => Path.Combine(run.Directory, e.FileName)), files);
        Assert.Empty(await _ffmpeg.GetDecodeErrorsAsync(fmp4 ? run.InitPath : null, files, cancellationToken));
    }

    [Fact]
    public async Task Transcode_FixedGopOfAVideoStartingOffTheGrid_CompletesEverySegment()
    {
        _ffmpeg.SkipIfUnavailable();
        var cancellationToken = TestContext.Current.CancellationToken;
        var plan = SegmentPlanner.CreateUniformPlan(Ticks(FfmpegFixture.SourceSeconds), Ticks(SegmentLength), 1 / FfmpegFixture.FrameDuration);
        using var run = CreateRun("run-gop", plan, 0, plan.Segments.Count, HlsCutMode.TranscodeVideo, false);
        using var job = CreateJob(run);

        // The encoders that cannot force keyframes get a GOP of a segment, counted from the first frame, which is 0.3 s late.
        var exit = Start(
            run,
            job,
            [
                "-i", _ffmpeg.LateVideoMkv, "-map", "0:v:0", "-map", "0:a:0",
                "-codec:v:0", "libx264", "-preset", "veryfast", "-bf", "2", "-sc_threshold:v:0", "0", "-g:v:0", "144", "-keyint_min:v:0", "144",
                "-codec:a:0", "aac", "-b:a", "96k"
            ],
            false,
            cancellationToken);

        var files = new List<string>();
        for (var index = 0; index < plan.Segments.Count; index++)
        {
            var location = await run.WaitForAsync(index, cancellationToken).WaitAsync(_timeout, cancellationToken);
            Assert.Equal(HlsSegmentState.Complete, location.State);
            files.AddRange(run.GetFiles(location));
        }

        Assert.Empty(await exit);

        // Each file starts late, and is the segment its start falls in.
        var list = new HlsListReader(run.ListPath);
        list.Read();
        Assert.InRange(list.Entries[1].StartTicks - plan.Segments[1].StartTicks, Ticks(0.25), Ticks(0.35));
        Assert.Equal(list.Entries.Select(e => Path.Combine(run.Directory, e.FileName)), files);
        Assert.Empty(await _ffmpeg.GetDecodeErrorsAsync(null, files, cancellationToken));
    }

    [Theory]
    [InlineData("mkv", true)]
    [InlineData("mp4", false)]
    [InlineData("ts", false)]
    public async Task Copy_RestartedRun_CompletesEverySegmentFromItsBoundary(string container, bool fmp4)
    {
        _ffmpeg.SkipIfUnavailable();
        var cancellationToken = TestContext.Current.CancellationToken;
        var source = _ffmpeg.GetSource(container);
        var keyframes = await _ffmpeg.GetKeyframesAsync(source, cancellationToken);
        var plan = SegmentPlanner.CreateKeyframePlan(
            new KeyframeData(Ticks(FfmpegFixture.SourceSeconds), keyframes.Select(Ticks).ToList()),
            Ticks(FfmpegFixture.SourceSeconds),
            Ticks(SegmentLength),
            1 / FfmpegFixture.FrameDuration);
        Assert.NotNull(plan);
        using var run = CreateRun($"run-copy-{container}", plan, 3, plan.Segments.Count, HlsCutMode.CopyVideo, fmp4);
        using var job = CreateJob(run);

        var exit = Start(
            run,
            job,
            [.. Seek(run), "-i", source, "-map", "0:v:0", "-map", "0:a:0", "-codec:v:0", "copy", "-codec:a:0", "copy"],
            fmp4,
            cancellationToken);

        if (fmp4)
        {
            Assert.True(await run.WaitForInitAsync(cancellationToken).WaitAsync(_timeout, cancellationToken));
        }

        var files = new List<string>();
        for (var index = run.StartIndex; index < plan.Segments.Count; index++)
        {
            var location = await run.WaitForAsync(index, cancellationToken).WaitAsync(_timeout, cancellationToken);
            Assert.Equal(HlsSegmentState.Complete, location.State);
            files.AddRange(run.GetFiles(location));
        }

        Assert.Empty(await exit);
        Assert.Equal(HlsRunState.Finished, run.State);

        // The seek lands before the boundary, on an earlier keyframe in an indexed container: the files before the boundary are
        // no segment's, and every file from it on is one segment's.
        var list = new HlsListReader(run.ListPath);
        list.Read();
        var boundary = plan.Segments[run.StartIndex].StartTicks - plan.ToleranceTicks;
        Assert.Equal(
            list.Entries.Skip(1).Where(e => e.StartTicks >= boundary).Select(e => Path.Combine(run.Directory, e.FileName)),
            files);
        Assert.True(list.Entries.Count > files.Count);
        Assert.Empty(await _ffmpeg.GetDecodeErrorsAsync(fmp4 ? run.InitPath : null, files, cancellationToken));
    }

    private static long Ticks(double seconds) => (long)Math.Round(seconds * TimeSpan.TicksPerSecond);

    private static string[] Seek(HlsEncoderRun run)
        => run.Start.InputSeekTicks > 0 ? ["-ss", HlsCuts.FormatSeconds(run.Start.InputSeekTicks)] : [];

    // The job of the transcode manager, which counts the request that starts it.
    private static TranscodingJob CreateJob(HlsEncoderRun run)
        => new(NullLogger<TranscodingJob>.Instance)
        {
            Path = run.ListPath,
            Type = TranscodingJobType.Hls,
            ActiveRequestCount = 1,
            CancellationTokenSource = new CancellationTokenSource()
        };

    private static async Task<string> ReportExitAsync(TranscodingJob job, Task<(int ExitCode, string Error)> ffmpeg)
    {
        var (exitCode, error) = await ffmpeg;
        job.ReportExited(exitCode);
        return error;
    }

    private HlsEncoderRun CreateRun(string name, SegmentPlan plan, int startIndex, int endIndex, HlsCutMode mode, bool fmp4)
        => new(plan, startIndex, endIndex, mode, _ffmpeg.CreateRunDirectory(name), fmp4 ? ".mp4" : ".ts", NullLogger.Instance);

    // Starts ffmpeg with the output options of the controller and attaches the run, as the session manager does.
    private Task<string> Start(HlsEncoderRun run, TranscodingJob job, List<string> arguments, bool fmp4, CancellationToken cancellationToken)
    {
        arguments.AddRange(
        [
            "-copyts", "-start_at_zero", "-avoid_negative_ts", "disabled", "-f", "segment", "-max_delay", "5000000", "-segment_time", "0.001",
            "-segment_list", run.ListPath, "-segment_list_type", "csv", "-individual_header_trailer", "0"
        ]);
        arguments.AddRange(fmp4
            ? ["-segment_format", "mp4", "-segment_header_filename", run.InitPath, "-segment_format_options", FragmentOptions]
            : ["-segment_format", "mpegts", "-segment_format_options", "avoid_negative_ts=disabled"]);
        arguments.Add(run.SegmentPattern);

        var exit = ReportExitAsync(job, _ffmpeg.TryFfmpegAsync(run.Directory, arguments, cancellationToken));
        run.Attach(job, job.CancellationTokenSource!);
        return exit;
    }
}
