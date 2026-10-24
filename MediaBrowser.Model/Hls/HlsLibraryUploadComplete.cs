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
public sealed record HlsLibraryUploadComplete(string? ShortcutTarget, string? OperationId = null);
