namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A directory that is due to be deleted.
/// </summary>
/// <param name="Base">The root of the HLS store the directory is in.</param>
/// <param name="RelativeDirectory">The directory, relative to the root.</param>
/// <param name="Attempts">How often deleting it failed.</param>
public sealed record HlsDeletionRecord(string Base, string RelativeDirectory, int Attempts);
