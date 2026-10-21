namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// What starting a job for media being added led to.
/// </summary>
public enum HlsEagerJobOutcome
{
    /// <summary>
    /// Nothing asked for is left to encode, so no job was started.
    /// </summary>
    Done = 0,

    /// <summary>
    /// The content has a job in progress, or another job holds its renditions.
    /// </summary>
    Busy = 1,

    /// <summary>
    /// A job was started and leased to the worker.
    /// </summary>
    Started = 2
}
