namespace MediaBrowser.Controller.Hls;

/// <summary>
/// A file of a published rendition that the server sends to a client.
/// </summary>
/// <param name="Location">Where the server reads it: a local path, or an http address of the storage.</param>
/// <param name="IsLocal">Whether the location is a local path.</param>
/// <param name="ContentType">The media type of the file.</param>
public sealed record HlsDeliveredFile(string Location, bool IsLocal, string ContentType);
