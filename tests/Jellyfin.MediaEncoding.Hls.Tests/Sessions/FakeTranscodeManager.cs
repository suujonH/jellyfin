using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jellyfin.MediaEncoding.Hls.Tests.Sessions;

/// <summary>
/// The request counting and killing of <c>TranscodeManager</c>, without ffmpeg: a killed job is cancelled and then exits.
/// </summary>
internal sealed class FakeTranscodeManager : ITranscodeManager
{
    private readonly List<TranscodingJob> _jobs = [];

    public List<TranscodingJob> Killed { get; } = [];

    /// <summary>Adds the job of a started process, which counts the request that started it.</summary>
    public TranscodingJob Add(string path, string? playSessionId, CancellationTokenSource cancellationTokenSource)
    {
        var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance)
        {
            Path = path,
            Type = TranscodingJobType.Hls,
            PlaySessionId = playSessionId,
            ActiveRequestCount = 1,
            CancellationTokenSource = cancellationTokenSource
        };
        lock (_jobs)
        {
            _jobs.Add(job);
        }

        return job;
    }

    public TranscodingJob? OnTranscodeBeginRequest(string path, TranscodingJobType type)
    {
        lock (_jobs)
        {
            var job = _jobs.FirstOrDefault(j => j.Type == type && string.Equals(j.Path, path, StringComparison.OrdinalIgnoreCase));
            job?.IncrementActiveRequestCount();
            return job;
        }
    }

    public void OnTranscodeEndRequest(TranscodingJob job) => job.DecrementActiveRequestCount();

    public async Task KillTranscodingJob(TranscodingJob job, bool deleteFiles)
    {
        lock (_jobs)
        {
            _jobs.Remove(job);
            Killed.Add(job);
        }

        if (job.CancellationTokenSource is { IsCancellationRequested: false } cancellationTokenSource)
        {
            await cancellationTokenSource.CancelAsync();
        }

        // ffmpeg ends on q without an error.
        if (!job.HasExited)
        {
            job.ReportExited(0);
        }
    }

    public TranscodingJob? GetTranscodingJob(string playSessionId) => throw new NotSupportedException();

    public TranscodingJob? GetTranscodingJob(string path, TranscodingJobType type) => throw new NotSupportedException();

    public void PingTranscodingJob(string playSessionId, bool? isUserPaused) => throw new NotSupportedException();

    public Task KillTranscodingJobs(string deviceId, string? playSessionId, Func<string, bool> deleteFiles) => throw new NotSupportedException();

    public void ReportTranscodingProgress(TranscodingJob job, StreamState state, TimeSpan? transcodingPosition, float? framerate, double? percentComplete, long? bytesTranscoded, int? bitRate)
        => throw new NotSupportedException();

    public Task<TranscodingJob> StartFfMpeg(StreamState state, string outputPath, string commandLineArguments, Guid userId, TranscodingJobType transcodingJobType, CancellationTokenSource cancellationTokenSource, string? workingDirectory = null)
        => throw new NotSupportedException();

    public ValueTask<IDisposable> LockAsync(string outputPath, CancellationToken cancellationToken) => throw new NotSupportedException();
}
