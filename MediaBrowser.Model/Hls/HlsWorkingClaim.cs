namespace MediaBrowser.Model.Hls;

/// <summary>
/// A worker marking a file it adds to a library as held by one of its operations.
/// </summary>
/// <param name="Location">The folder of the library, as <c>/Library/VirtualFolders</c> names it; <c>null</c> when the library has one.</param>
/// <param name="Path">The path the file goes to in the folder, with forward slashes, as it is uploaded.</param>
/// <param name="OperationId">The operation, as 32 lower-case hexadecimal characters.</param>
public sealed record HlsWorkingClaim(string? Location, string Path, string OperationId);
