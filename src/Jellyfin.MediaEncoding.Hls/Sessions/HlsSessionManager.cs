using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Plans;
using MediaBrowser.Common;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.MediaEncoding.Hls.Sessions;

/// <summary>
/// Keeps the live HLS sessions and decides when a segment request starts a new run.
/// </summary>
public sealed class HlsSessionManager : IDisposable
{
    // A run that transcodes the video forces a keyframe at each of its boundaries, listed on the command line;
    // this many stay far below the 32K characters of a Windows command line.
    internal const int MaxTranscodeSegments = 600;

    private static readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromMinutes(5);

    // A segment further ahead of what the run has completed is reached sooner by a new run.
    private static readonly long _maxGapTicks = TimeSpan.FromSeconds(24).Ticks;

    private readonly ConcurrentDictionary<string, HlsSession> _sessions = new(StringComparer.Ordinal);
    private readonly ITranscodeManager _transcodeManager;
    private readonly ILogger<HlsSessionManager> _logger;
    private readonly Timer _sweepTimer;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsSessionManager"/> class.
    /// </summary>
    /// <param name="transcodeManager">Instance of the <see cref="ITranscodeManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{HlsSessionManager}"/> interface.</param>
    public HlsSessionManager(ITranscodeManager transcodeManager, ILogger<HlsSessionManager> logger)
    {
        _transcodeManager = transcodeManager;
        _logger = logger;
        _sweepTimer = new Timer(_ => Sweep(), null, _sweepInterval, _sweepInterval);
    }

    /// <summary>
    /// Gets the session of a variant.
    /// </summary>
    /// <param name="key">The key of the variant.</param>
    /// <returns>The session, or null when there is none.</returns>
    public HlsSession? GetSession(string key)
    {
        if (_sessions.TryGetValue(key, out var session))
        {
            session.Touch();
            return session;
        }

        return null;
    }

    /// <summary>
    /// Adds a session, unless one of the same variant was added first.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <returns>The session of the variant.</returns>
    public HlsSession AddSession(HlsSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var added = _sessions.GetOrAdd(session.Key, session);
        if (!ReferenceEquals(added, session))
        {
            session.Dispose();
        }

        added.Touch();
        return added;
    }

    /// <summary>
    /// Gets a complete segment, starting a run when the current one cannot produce it soon.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="index">The segment.</param>
    /// <param name="starter">Starts the ffmpeg process of a run.</param>
    /// <param name="cancellationToken">The cancellation token of the request.</param>
    /// <returns>The segment, to release once it is sent.</returns>
    /// <exception cref="ResourceNotFoundException">The media ends before the segment.</exception>
    /// <exception cref="FfmpegException">The run failed or stopped before the segment.</exception>
    public async Task<HlsSegmentLease> GetSegmentAsync(
        HlsSession session,
        int index,
        Func<HlsEncoderRun, CancellationTokenSource, Task<TranscodingJob>> starter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(starter);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, session.Plan.Segments.Count);

        session = await LockAsync(session, cancellationToken).ConfigureAwait(false);
        try
        {
            var (run, job) = await GetRunAsync(session, index, starter, cancellationToken).ConfigureAwait(false);
            HlsSegmentLocation location;
            try
            {
                if (job.TranscodingThrottler is { } throttler)
                {
                    await throttler.UnpauseTranscoding().ConfigureAwait(false);
                }

                location = await run.WaitForAsync(index, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _transcodeManager.OnTranscodeEndRequest(job);
                throw;
            }

            if (location.State == HlsSegmentState.Complete)
            {
                return new HlsSegmentLease(_transcodeManager, job, session.Plan.Segments[index], run.GetFiles(location));
            }

            _transcodeManager.OnTranscodeEndRequest(job);
            throw location.State switch
            {
                HlsSegmentState.Absent => new ResourceNotFoundException($"The media ends before HLS segment {index}."),
                HlsSegmentState.Skipped => new FfmpegException($"HLS run {run.Id} starts after segment {index}."),
                _ => new FfmpegException($"HLS run {run.Id} stopped before segment {index}.")
            };
        }
        finally
        {
            session.Lock.Release();
        }
    }

    /// <summary>
    /// Gets the init segment of a fMP4 session, starting a run at the first segment when there is none.
    /// </summary>
    /// <param name="session">The session.</param>
    /// <param name="starter">Starts the ffmpeg process of a run.</param>
    /// <param name="cancellationToken">The cancellation token of the request.</param>
    /// <returns>The path of the init segment, which stays for the whole session.</returns>
    /// <exception cref="ResourceNotFoundException">The session is not fMP4.</exception>
    /// <exception cref="FfmpegException">The run stopped before writing its init segment.</exception>
    public async Task<string> GetInitAsync(
        HlsSession session,
        Func<HlsEncoderRun, CancellationTokenSource, Task<TranscodingJob>> starter,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(starter);
        if (!session.IsFragmented)
        {
            throw new ResourceNotFoundException("MPEG-TS segments have no init segment.");
        }

        session = await LockAsync(session, cancellationToken).ConfigureAwait(false);
        try
        {
            if (File.Exists(session.InitPath))
            {
                return session.InitPath;
            }

            // Every run writes the same init segment; the one there is serves, or a run from the start.
            var run = session.Run;
            var job = run is null || run.State is HlsRunState.Failed or HlsRunState.Stopped
                ? null
                : _transcodeManager.OnTranscodeBeginRequest(run.ListPath, TranscodingJobType.Hls);
            if (run is null || job is null)
            {
                run = await StartRunAsync(session, 0, starter, cancellationToken).ConfigureAwait(false);
                job = run.Job!;
            }

            try
            {
                if (!await run.WaitForInitAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new FfmpegException($"HLS run {run.Id} stopped before writing its init segment.");
                }

                File.Copy(run.InitPath, session.InitPath, true);
                return session.InitPath;
            }
            finally
            {
                _transcodeManager.OnTranscodeEndRequest(job);
            }
        }
        finally
        {
            session.Lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _sweepTimer.Dispose();
        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    /// <summary>
    /// Waits until a transcoding job has no requests: those that send its files.
    /// </summary>
    /// <param name="job">The job.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes when the job has no requests.</returns>
    internal static async Task WaitForActiveTranscodingRequests(TranscodingJob? job, CancellationToken cancellationToken)
    {
        while (job?.ActiveRequestCount > 0)
        {
            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    // Takes the lock of a session; one that expired meanwhile is added back, unless another session of its variant came first.
    private async Task<HlsSession> LockAsync(HlsSession session, CancellationToken cancellationToken)
    {
        await session.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        while (session.IsClosed)
        {
            var current = _sessions.GetOrAdd(session.Key, session);
            if (ReferenceEquals(current, session))
            {
                session.IsClosed = false;
                break;
            }

            session.Lock.Release();
            session = current;
            await session.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        session.Touch();
        return session;
    }

    // Gets the run to wait for the segment on, with a request counted on its job.
    private async Task<(HlsEncoderRun Run, TranscodingJob Job)> GetRunAsync(
        HlsSession session,
        int index,
        Func<HlsEncoderRun, CancellationTokenSource, Task<TranscodingJob>> starter,
        CancellationToken cancellationToken)
    {
        var run = session.Run;
        if (run is not null && !NeedsRestart(session, run, index))
        {
            // Null once the job was killed: at the end of playback, or because the client stopped asking.
            var job = _transcodeManager.OnTranscodeBeginRequest(run.ListPath, TranscodingJobType.Hls);
            if (job is not null)
            {
                return (run, job);
            }
        }

        // The new job counts the request that starts it.
        run = await StartRunAsync(session, index, starter, cancellationToken).ConfigureAwait(false);
        return (run, run.Job!);
    }

    private bool NeedsRestart(HlsSession session, HlsEncoderRun run, int index)
    {
        if (run.State is HlsRunState.Failed or HlsRunState.Stopped || index < run.StartIndex || index >= run.EndIndex)
        {
            return true;
        }

        var location = run.Locate(index);
        switch (location.State)
        {
            case HlsSegmentState.Complete:
                // The segment cleaner deletes what lies far behind playback.
                return !run.GetFiles(location).All(File.Exists);
            case HlsSegmentState.Pending:
                var gap = session.Plan.Segments[index].StartTicks - run.CompletedPositionTicks;
                if (gap > _maxGapTicks)
                {
                    _logger.LogDebug("Starting HLS session {SessionId} again at segment {Index}, {Gap} ahead of run {RunId}", session.Id, index, TimeSpan.FromTicks(gap), run.Id);
                    return true;
                }

                return false;
            default:
                // A new run ends at the same end.
                return false;
        }
    }

    private async Task<HlsEncoderRun> StartRunAsync(
        HlsSession session,
        int index,
        Func<HlsEncoderRun, CancellationTokenSource, Task<TranscodingJob>> starter,
        CancellationToken cancellationToken)
    {
        await EndRunAsync(session, true, cancellationToken).ConfigureAwait(false);
        await StopOtherVariantsAsync(session).ConfigureAwait(false);

        var count = session.Plan.Segments.Count;
        var end = session.Mode == HlsCutMode.TranscodeVideo ? Math.Min(index + MaxTranscodeSegments, count) : count;
        var run = new HlsEncoderRun(session.Plan, index, end, session.Mode, session.Directory, session.SegmentExtension, _logger);
        _logger.LogDebug("Starting HLS run {RunId} of session {SessionId} at segment {Index}, {Mode}", run.Id, session.Id, index, session.Mode);

        // CTS lifecycle is managed internally: the transcoding job disposes it when ffmpeg exits.
        var cancellationTokenSource = new CancellationTokenSource();
        TranscodingJob job;
        try
        {
            job = await starter(run, cancellationTokenSource).ConfigureAwait(false);
        }
        catch
        {
            _ = DeleteFilesAsync(run);
            run.Dispose();
            throw;
        }

        run.Attach(job, cancellationTokenSource);
        session.Run = run;
        return run;
    }

    // Stops the run of a session and deletes its files once the requests that send them have ended.
    private async Task EndRunAsync(HlsSession session, bool waitForRequests, CancellationToken cancellationToken)
    {
        var run = session.Run;
        if (run is null)
        {
            return;
        }

        if (run.Job is { } job)
        {
            if (waitForRequests)
            {
                await WaitForActiveTranscodingRequests(job, cancellationToken).ConfigureAwait(false);
            }

            await _transcodeManager.KillTranscodingJob(job, false).ConfigureAwait(false);
        }

        session.Run = null;
        _ = DeleteFilesAsync(run);
        run.Dispose();
    }

    // A playback plays one variant at a time; like the transcodes before, the run of another stops when this one starts.
    private async Task StopOtherVariantsAsync(HlsSession session)
    {
        if (string.IsNullOrEmpty(session.PlaySessionId) && string.IsNullOrEmpty(session.DeviceId))
        {
            return;
        }

        foreach (var other in _sessions.Values)
        {
            var isSamePlayback = !string.IsNullOrEmpty(session.PlaySessionId)
                ? string.Equals(other.PlaySessionId, session.PlaySessionId, StringComparison.OrdinalIgnoreCase)
                : string.Equals(other.DeviceId, session.DeviceId, StringComparison.OrdinalIgnoreCase);
            if (!ReferenceEquals(other, session) && isSamePlayback && other.Run?.Job is { HasExited: false } job)
            {
                // Its files stay for the requests that send them; its session deletes them with the run.
                await _transcodeManager.KillTranscodingJob(job, false).ConfigureAwait(false);
            }
        }
    }

    private async Task DeleteFilesAsync(HlsEncoderRun run)
    {
        // Like the transcode manager: ffmpeg may still hold the files for a moment after it is stopped.
        await Task.Delay(1500).ConfigureAwait(false);
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                run.DeleteFiles();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == 10)
                {
                    _logger.LogError(ex, "Error deleting the files of HLS run {RunId}", run.Id);
                    return;
                }

                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }

    private void Sweep()
    {
        var expiry = DateTime.UtcNow - _idleTimeout;
        foreach (var session in _sessions.Values)
        {
            // A session in use is not idle; it is looked at again on the next sweep.
            if (session.LastAccess < expiry && session.Lock.Wait(0))
            {
                _ = CloseAsync(session, expiry);
            }
        }
    }

    // Called with the lock of the session held.
    private async Task CloseAsync(HlsSession session, DateTime expiry)
    {
        try
        {
            if (session.LastAccess >= expiry)
            {
                return;
            }

            _logger.LogDebug("Closing idle HLS session {SessionId}", session.Id);
            session.IsClosed = true;
            _sessions.TryRemove(new KeyValuePair<string, HlsSession>(session.Key, session));
            await EndRunAsync(session, false, CancellationToken.None).ConfigureAwait(false);
            TryDelete(session.InitPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error closing HLS session {SessionId}", session.Id);
        }
        finally
        {
            session.Lock.Release();
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Error deleting {Path}", path);
        }
    }
}
