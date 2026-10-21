namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// What recording a library file changed.
/// </summary>
public enum HlsSourceChange
{
    /// <summary>
    /// The file is recorded as it was.
    /// </summary>
    Unchanged = 0,

    /// <summary>
    /// The file was not recorded yet.
    /// </summary>
    Inserted = 1,

    /// <summary>
    /// The path, size or modification time changed, so the file is new content and its hash was cleared.
    /// </summary>
    ContentChanged = 2
}
