using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Plans;
using Jellyfin.MediaEncoding.Hls.Sessions;
using Jellyfin.MediaEncoding.Keyframes;
using MediaBrowser.Common;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Sessions;

/// <summary>
/// The restart policy and request counting of live HLS, with runs whose lists and files the tests write as ffmpeg would.
/// </summary>
public sealed class HlsSessionManagerTests : IDisposable
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(10);

    // Segments of 6 s at 25 fps, from 0 to 60 s.
    private static readonly SegmentPlan _uniformPlan = SegmentPlanner.CreateUniformPlan(Seconds(60), Seconds(6), 25);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jellyfin-hls-sessions-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
    private readonly FakeTranscodeManager _transcodeManager = new();
    private readonly Channel<HlsEncoderRun> _runs = Channel.CreateUnbounded<HlsEncoderRun>();

    // Ends the requests a test leaves waiting.
    private readonly CancellationTokenSource _cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
    private readonly HlsSessionManager _manager;

    public HlsSessionManagerTests()
    {
        Directory.CreateDirectory(_directory);
        _manager = new HlsSessionManager(_transcodeManager, NullLogger<HlsSessionManager>.Instance);
    }

    public void Dispose()
    {
        _cancellation.Cancel();
        _cancellation.Dispose();
        _manager.Dispose();
        try
        {
            Directory.Delete(_directory, true);
        }
        catch (IOException)
        {
            // A file deleted in the background meanwhile; the temporary directory is harmless.
        }
    }

    [Fact]
    public async Task GetSegment_NoRun_StartsOneAtTheSegment()
    {
        var session = AddSession("a", _uniformPlan, HlsCutMode.TranscodeVideo);

        var request = GetSegmentAsync(session, 3);
        var run = await NextRunAsync();
        Assert.Equal(3, run.StartIndex);
        Assert.Equal(HlsCutMode.TranscodeVideo, run.Mode);

        // The first file of a transcode is its first segment.
        Write(run, 0, 0, 24);
        Write(run, 1, 24, 30);
        var lease = await request.WaitAsync(_timeout, TestContext.Current.CancellationToken);

        Assert.Equal(3, lease.Segment.Index);
        Assert.Equal([FilePath(run, 0)], lease.Files);
        Assert.Equal(1, run.Job!.ActiveRequestCount);

        lease.Release();
        lease.Release();
        Assert.Equal(0, run.Job.ActiveRequestCount);
        Assert.Equal(Seconds(24), run.Job.DownloadPositionTicks);
    }

    [Fact]
    public async Task GetSegment_NextSegmentOfTheRun_WaitsForItsFiles()
    {
        var session = AddSession("a", _uniformPlan, HlsCutMode.TranscodeVideo);
        var first = GetSegmentAsync(session, 0);
        var run = await NextRunAsync();
        Write(run, 0, 0, 6);
        Write(run, 1, 6, 12);
        (await first.WaitAsync(_timeout, TestContext.Current.CancellationToken)).Release();

        var second = GetSegmentAsync(session, 1);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(second.IsCompleted);

        Write(run, 2, 12, 18);
        var lease = await second.WaitAsync(_timeout, TestContext.Current.CancellationToken);

        Assert.Equal([FilePath(run, 1)], lease.Files);
        Assert.False(_runs.Reader.TryRead(out _));
        Assert.Empty(_transcodeManager.Killed);
    }

    [Fact]
    public async Task GetSegment_BeforeTheRun_StartsAgainOnceTheRunIsNoLongerSent()
    {
        var session = AddSession("a", _uniformPlan, HlsCutMode.TranscodeVideo);
        var first = GetSegmentAsync(session, 5);
        var run = await NextRunAsync();
        Write(run, 0, 0, 36);
        Write(run, 1, 36, 42);
        var lease = await first.WaitAsync(_timeout, TestContext.Current.CancellationToken);

        var second = GetSegmentAsync(session, 2);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(_runs.Reader.TryRead(out _));

        lease.Release();
        var restarted = await NextRunAsync();

        Assert.Equal(2, restarted.StartIndex);
        Assert.Equal([run.Job!], _transcodeManager.Killed);
        Write(restarted, 0, 0, 18);
        Write(restarted, 1, 18, 24);
        Assert.Equal([FilePath(restarted, 0)], (await second.WaitAsync(_timeout, TestContext.Current.CancellationToken)).Files);
    }

    [Fact]
    public async Task GetSegment_FarAheadOfTheRun_StartsAgain()
    {
        var session = AddSession("a", _uniformPlan, HlsCutMode.TranscodeVideo);
        var first = GetSegmentAsync(session, 0);
        var run = await NextRunAsync();
        Write(run, 0, 0, 6);
        Write(run, 1, 6, 12);
        (await first.WaitAsync(_timeout, TestContext.Current.CancellationToken)).Release();

        // 30 s after what the run has completed.
        _ = GetSegmentAsync(session, 6);
        var restarted = await NextRunAsync();

        Assert.Equal(6, restarted.StartIndex);
    }

    [Fact]
    public async Task GetSegment_ShortlyAheadOfTheRun_WaitsAndEndsItsRequestWhenCancelled()
    {
        var session = AddSession("a", _uniformPlan, HlsCutMode.TranscodeVideo);
        var first = GetSegmentAsync(session, 0);
        var run = await NextRunAsync();
        Write(run, 0, 0, 6);
        Write(run, 1, 6, 12);
        (await first.WaitAsync(_timeout, TestContext.Current.CancellationToken)).Release();

        using var cancellationTokenSource = new CancellationTokenSource();
        var request = _manager.GetSegmentAsync(session, 4, StartAsync, cancellationTokenSource.Token);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.False(_runs.Reader.TryRead(out _));
        Assert.Equal(1, run.Job!.ActiveRequestCount);

        await cancellationTokenSource.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(_timeout, TestContext.Current.CancellationToken));
        Assert.Equal(0, run.Job.ActiveRequestCount);
    }

    [Fact]
    public async Task GetSegment_CopyWithoutTheKeyframeOfTheSegment_SendsTheFileStartingInIt()
    {
        var plan = SegmentPlanner.CreateKeyframePlan(new KeyframeData(Seconds(30), [0, Seconds(6), Seconds(12), Seconds(18), Seconds(24)]), Seconds(30), Seconds(6), 25)!;
        var session = AddSession("a", plan, HlsCutMode.CopyVideo);

        var request = GetSegmentAsync(session, 2);
        var copy = await NextRunAsync();
        Assert.Null(copy.Start.FirstFileStartTicks);

        // The file of segment 2 starts a second late; the copy goes on.
        Write(copy, 0, 0, 6);
        Write(copy, 1, 6, 13);
        Write(copy, 2, 13, 18);
        Write(copy, 3, 18, 24);

        Assert.Equal([FilePath(copy, 2)], (await request.WaitAsync(_timeout, TestContext.Current.CancellationToken)).Files);
        Assert.False(_runs.Reader.TryRead(out _));
        Assert.Empty(_transcodeManager.Killed);
    }

    [Fact]
    public async Task GetSegment_RunFails_ThrowsAndTheNextRequestStartsAgain()
    {
        var session = AddSession("a", _uniformPlan, HlsCutMode.TranscodeVideo);
        var request = GetSegmentAsync(session, 0);
        var run = await NextRunAsync();

        run.Job!.ReportExited(1);

        await Assert.ThrowsAsync<FfmpegException>(() => request.WaitAsync(_timeout, TestContext.Current.CancellationToken));
        Assert.Equal(0, run.Job.ActiveRequestCount);

        _ = GetSegmentAsync(session, 0);
        Assert.Equal(0, (await NextRunAsync()).StartIndex);
    }

    [Fact]
    public async Task GetSegment_MediaEndsBeforeTheSegment_IsNotFound()
    {
        var session = AddSession("a", _uniformPlan, HlsCutMode.TranscodeVideo);
        var first = GetSegmentAsync(session, 0);
        var run = await NextRunAsync();
        Write(run, 0, 0, 6);
        Write(run, 1, 6, 12);
        Write(run, 2, 12, 17);
        run.Job!.ReportExited(0);
        (await first.WaitAsync(_timeout, TestContext.Current.CancellationToken)).Release();

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => GetSegmentAsync(session, 3).WaitAsync(_timeout, TestContext.Current.CancellationToken));
        Assert.False(_runs.Reader.TryRead(out _));
    }

    [Fact]
    public async Task GetSegment_FilesDeletedBehindPlayback_StartsAgain()
    {
        var session = AddSession("a", _uniformPlan, HlsCutMode.TranscodeVideo);
        var first = GetSegmentAsync(session, 0);
        var run = await NextRunAsync();
        Write(run, 0, 0, 6);
        Write(run, 1, 6, 12);
        (await first.WaitAsync(_timeout, TestContext.Current.CancellationToken)).Release();

        File.Delete(FilePath(run, 0));
        _ = GetSegmentAsync(session, 0);

        Assert.Equal(0, (await NextRunAsync()).StartIndex);
        Assert.Equal([run.Job!], _transcodeManager.Killed);
    }

    [Fact]
    public async Task GetSegment_StartingARun_StopsTheRunOfAnotherVariantOfThePlayback()
    {
        var first = AddSession("a", _uniformPlan, HlsCutMode.TranscodeVideo);
        var second = AddSession("b", _uniformPlan, HlsCutMode.TranscodeVideo);
        _ = GetSegmentAsync(first, 0);
        var run = await NextRunAsync();

        _ = GetSegmentAsync(second, 0);
        await NextRunAsync();

        Assert.Equal([run.Job!], _transcodeManager.Killed);
    }

    [Fact]
    public async Task GetSegment_CompletedSegments_AdvanceThePositionOfTheJob()
    {
        var session = AddSession("a", _uniformPlan, HlsCutMode.TranscodeVideo);
        _ = GetSegmentAsync(session, 0);
        var run = await NextRunAsync();

        Write(run, 0, 0, 6);
        Write(run, 1, 6, 12);
        Write(run, 2, 12, 18);

        // The throttler compares it with the download position; the run reads its list as the files appear.
        using var timeout = new CancellationTokenSource(_timeout);
        while (run.Job!.CompletedPositionTicks != Seconds(12))
        {
            await Task.Delay(50, timeout.Token);
        }
    }

    [Fact]
    public async Task GetFilesThrough_FileAcrossLaterSegments_IsKeptForThem()
    {
        var session = AddSession("a", _uniformPlan, HlsCutMode.TranscodeVideo);
        var request = GetSegmentAsync(session, 0);
        var run = await NextRunAsync();

        // File 1 holds segments 1 to 4.
        Write(run, 0, 0, 6);
        Write(run, 1, 6, 30);
        Write(run, 2, 30, 36);
        Write(run, 3, 36, 42);
        (await request.WaitAsync(_timeout, TestContext.Current.CancellationToken)).Release();
        run.Locate(0);

        Assert.Equal([FilePath(run, 0)], run.GetFilesThrough(0));
        Assert.Equal([FilePath(run, 0)], run.GetFilesThrough(3));
        Assert.Equal([FilePath(run, 0), FilePath(run, 1)], run.GetFilesThrough(4));

        // The last file listed may hold the segments after it.
        Assert.Equal([FilePath(run, 0), FilePath(run, 1), FilePath(run, 2)], run.GetFilesThrough(9));
    }

    [Fact]
    public async Task GetInit_NoRun_StartsOneAndKeepsTheFirstInitSegment()
    {
        var session = AddSession("a", _uniformPlan, HlsCutMode.TranscodeVideo, ".mp4");

        var request = _manager.GetInitAsync(session, StartAsync, TestContext.Current.CancellationToken);
        var run = await NextRunAsync();
        Assert.Equal(0, run.StartIndex);

        // The muxer closes the init segment before it opens the first file.
        await File.WriteAllTextAsync(run.InitPath, "init", TestContext.Current.CancellationToken);
        Write(run, 0, 0, 6, ".mp4");
        var path = await request.WaitAsync(_timeout, TestContext.Current.CancellationToken);

        Assert.Equal(session.InitPath, path);
        Assert.Equal("init", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(0, run.Job!.ActiveRequestCount);

        Assert.Equal(path, await _manager.GetInitAsync(session, StartAsync, TestContext.Current.CancellationToken).WaitAsync(_timeout, TestContext.Current.CancellationToken));
        Assert.False(_runs.Reader.TryRead(out _));
    }

    [Fact]
    public async Task GetInit_MpegTs_IsNotFound()
    {
        var session = AddSession("a", _uniformPlan, HlsCutMode.TranscodeVideo);

        await Assert.ThrowsAsync<ResourceNotFoundException>(() => _manager.GetInitAsync(session, StartAsync, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AddSession_SameVariant_KeepsTheFirst()
    {
        var first = AddSession("a", _uniformPlan, HlsCutMode.TranscodeVideo);
        var second = _manager.AddSession(new HlsSession("a", _uniformPlan, HlsCutMode.TranscodeVideo, _directory, ".ts", "play", "device"));

        Assert.Same(first, second);
        Assert.Same(first, _manager.GetSession("a"));
        Assert.Null(_manager.GetSession("b"));
    }

    [Fact]
    public async Task WaitForActiveTranscodingRequests_WaitsUntilRequestCompletes()
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            ActiveRequestCount = 1
        };

        var waitTask = HlsSessionManager.WaitForActiveTranscodingRequests(job, CancellationToken.None);
        Assert.False(waitTask.IsCompleted);

        job.DecrementActiveRequestCount();

        await waitTask;
    }

    [Fact]
    public async Task WaitForActiveTranscodingRequests_WaitsForEveryRequest()
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            ActiveRequestCount = 2
        };

        var waitTask = HlsSessionManager.WaitForActiveTranscodingRequests(job, CancellationToken.None);
        job.DecrementActiveRequestCount();

        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.False(waitTask.IsCompleted);

        job.DecrementActiveRequestCount();

        await waitTask;
    }

    [Fact]
    public async Task WaitForActiveTranscodingRequests_ReturnsWithoutAnActiveRequest()
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance);

        await HlsSessionManager.WaitForActiveTranscodingRequests(job, CancellationToken.None);
        await HlsSessionManager.WaitForActiveTranscodingRequests(null, CancellationToken.None);
    }

    [Fact]
    public async Task WaitForActiveTranscodingRequests_ObservesCancellation()
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            ActiveRequestCount = 1
        };
        using var cancellationTokenSource = new CancellationTokenSource();

        var waitTask = HlsSessionManager.WaitForActiveTranscodingRequests(job, cancellationTokenSource.Token);
        await cancellationTokenSource.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);
    }

    private static long Seconds(double value) => (long)Math.Round(value * TimeSpan.TicksPerSecond);

    private static string FilePath(HlsEncoderRun run, int number, string extension = ".ts")
        => Path.Combine(run.Directory, run.Id + "-" + number.ToString(CultureInfo.InvariantCulture) + extension);

    // Writes a GOP file and its line, as the muxer does; the first file of a run is listed from 0.
    private static void Write(HlsEncoderRun run, int number, double start, double end, string extension = ".ts")
    {
        File.WriteAllText(FilePath(run, number, extension), "gop " + number.ToString(CultureInfo.InvariantCulture));
        File.AppendAllText(
            run.ListPath,
            string.Create(CultureInfo.InvariantCulture, $"{run.Id}-{number}{extension},{(number == 0 ? 0 : start):F6},{end:F6}\n"));
    }

    private HlsSession AddSession(string key, SegmentPlan plan, HlsCutMode mode, string extension = ".ts")
        => _manager.AddSession(new HlsSession(key, plan, mode, _directory, extension, "play", "device"));

    private Task<HlsSegmentLease> GetSegmentAsync(HlsSession session, int index)
        => _manager.GetSegmentAsync(session, index, StartAsync, _cancellation.Token);

    private async Task<HlsEncoderRun> NextRunAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(_timeout);
        return await _runs.Reader.ReadAsync(timeout.Token);
    }

    // ffmpeg creates the list before any file.
    private async Task<TranscodingJob> StartAsync(HlsEncoderRun run, CancellationTokenSource cancellationTokenSource)
    {
        await File.WriteAllTextAsync(run.ListPath, string.Empty, CancellationToken.None);
        var job = _transcodeManager.Add(run.ListPath, "play", cancellationTokenSource);
        _runs.Writer.TryWrite(run);
        return job;
    }
}
