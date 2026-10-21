namespace Jellyfin.MediaEncoding.Hls.Sessions;

/// <summary>
/// The state of the ffmpeg process of a run.
/// </summary>
internal enum HlsRunState
{
    /// <summary>
    /// ffmpeg is running.
    /// </summary>
    Running,

    /// <summary>
    /// ffmpeg ended the run itself, without an error: the list is whole and every file closed.
    /// </summary>
    Finished,

    /// <summary>
    /// ffmpeg failed.
    /// </summary>
    Failed,

    /// <summary>
    /// The run was stopped: by the session, at the end of playback or because the client stopped asking.
    /// </summary>
    Stopped
}
