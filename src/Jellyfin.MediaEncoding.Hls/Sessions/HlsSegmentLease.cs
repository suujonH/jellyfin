using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.MediaEncoding.Hls.Plans;
using MediaBrowser.Controller.MediaEncoding;

namespace Jellyfin.MediaEncoding.Hls.Sessions;

/// <summary>
/// A complete segment that a request sends. The request counts on the transcoding job of its run, so the run, and its files,
/// stay until it is released.
/// </summary>
public sealed class HlsSegmentLease
{
    private readonly ITranscodeManager _transcodeManager;
    private readonly TranscodingJob _job;
    private int _released;

    internal HlsSegmentLease(ITranscodeManager transcodeManager, TranscodingJob job, HlsSegment segment, IReadOnlyList<string> files)
    {
        _transcodeManager = transcodeManager;
        _job = job;
        Segment = segment;
        Files = files;
    }

    /// <summary>
    /// Gets the segment.
    /// </summary>
    public HlsSegment Segment { get; }

    /// <summary>
    /// Gets the GOP files of the segment, sent one after another.
    /// </summary>
    public IReadOnlyList<string> Files { get; }

    /// <summary>
    /// Records that the segment was sent, for the throttler and the segment cleaner, and ends the request on the job.
    /// Only the first call counts.
    /// </summary>
    public void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        var end = Segment.EndTicks;
        _job.DownloadPositionTicks = Math.Max(_job.DownloadPositionTicks ?? end, end);
        _job.ReportSegmentDownloaded(Segment.Index, end);
        _transcodeManager.OnTranscodeEndRequest(_job);
    }
}
