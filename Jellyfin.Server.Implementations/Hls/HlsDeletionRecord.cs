namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A directory that is due to be deleted.
/// </summary>
/// <param name="RelativeDirectory">The directory.</param>
/// <param name="Attempts">How often deleting it failed.</param>
public sealed record HlsDeletionRecord(string RelativeDirectory, int Attempts);
