namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// What identifying the file of a job led to.
/// </summary>
public enum HlsIdentifyOutcome
{
    /// <summary>
    /// The lease is not valid.
    /// </summary>
    LeaseLost = 0,

    /// <summary>
    /// The file is no longer the one the job was claimed for; the job waits again or was cancelled.
    /// </summary>
    SourceChanged = 1,

    /// <summary>
    /// Nothing is left to encode, so the job is complete.
    /// </summary>
    Done = 2,

    /// <summary>
    /// The job encodes the renditions of its plan.
    /// </summary>
    Planned = 3
}
