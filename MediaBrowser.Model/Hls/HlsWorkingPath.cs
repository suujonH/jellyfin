namespace MediaBrowser.Model.Hls;

/// <summary>
/// A file a worker adds to a library, whose working marker is read.
/// </summary>
/// <param name="Location">The folder of the library, as <c>/Library/VirtualFolders</c> names it; <c>null</c> when the library has one.</param>
/// <param name="Path">The path the file goes to in the folder, with forward slashes.</param>
public sealed record HlsWorkingPath(string? Location, string Path);
