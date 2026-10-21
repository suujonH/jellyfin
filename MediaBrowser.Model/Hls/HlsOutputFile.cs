namespace MediaBrowser.Model.Hls;

/// <summary>
/// A file of an encoded rendition.
/// </summary>
/// <param name="Path">The name of the file in the directory of the rendition.</param>
/// <param name="Length">The size, in bytes.</param>
public sealed record HlsOutputFile(string Path, long Length);
