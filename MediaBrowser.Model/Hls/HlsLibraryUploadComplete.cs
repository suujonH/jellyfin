namespace MediaBrowser.Model.Hls;

/// <summary>
/// The end of a file written into a library folder.
/// </summary>
/// <param name="ShortcutTarget">
/// The target of the .strm file, when what was uploaded is the zip of its TrueCache entry; <c>null</c> for a plain file.
/// </param>
/// <param name="OperationId">
/// The operation that holds the file; when given, the file is put in place only while its working marker names this operation.
/// </param>
/// <param name="Sha256">
/// The SHA-256 of the media content, hex: of the file itself for a plain file, of the target for a .strm file; with
/// <c>ContentLength</c>, lets the server link the library item to the content without a source job.
/// </param>
/// <param name="ContentLength">The size of that content, in bytes.</param>
public sealed record HlsLibraryUploadComplete(string? ShortcutTarget, string? OperationId = null, string? Sha256 = null, long? ContentLength = null);
