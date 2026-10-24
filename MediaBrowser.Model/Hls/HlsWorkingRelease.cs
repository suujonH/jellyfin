namespace MediaBrowser.Model.Hls;

/// <summary>
/// A worker letting go of a file it added; the marker is removed only while it names the operation.
/// </summary>
/// <param name="Location">The folder of the library, as <c>/Library/VirtualFolders</c> names it; <c>null</c> when the library has one.</param>
/// <param name="Path">The path of the file in the folder, with forward slashes.</param>
/// <param name="OperationId">The operation that held it.</param>
public sealed record HlsWorkingRelease(string? Location, string Path, string OperationId);
