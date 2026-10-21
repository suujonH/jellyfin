namespace MediaBrowser.Model.NetworkStorage;

/// <summary>
/// What found that a resource could not be read.
/// </summary>
public enum NetworkStorageFailureSource
{
    /// <summary>
    /// Streaming or transcoding it for a client.
    /// </summary>
    Playback = 0,

    /// <summary>
    /// Probing it during a library scan.
    /// </summary>
    Probe = 1,

    /// <summary>
    /// The storage check started from the dashboard.
    /// </summary>
    Check = 2
}
