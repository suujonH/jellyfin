namespace MediaBrowser.Model.Hls;

/// <summary>
/// A file being written into a library folder.
/// </summary>
/// <param name="UploadId">The id the chunks are sent to.</param>
public sealed record HlsLibraryUpload(string UploadId);
