namespace MediaBrowser.Model.Hls;

/// <summary>
/// The status of a job of pre-encoded HLS.
/// </summary>
public enum HlsJobStatus
{
    /// <summary>
    /// The job waits for a worker.
    /// </summary>
    Pending = 0,

    /// <summary>
    /// A worker holds the lease of the job.
    /// </summary>
    Running = 1,

    /// <summary>
    /// The worker reported its outputs, which wait to be verified.
    /// </summary>
    Verifying = 2,

    /// <summary>
    /// The job is done.
    /// </summary>
    Complete = 3,

    /// <summary>
    /// The job failed too often.
    /// </summary>
    Failed = 4,

    /// <summary>
    /// The job was cancelled.
    /// </summary>
    Cancelled = 5
}
