using System;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// A worker starting to write a file into a library folder.
/// </summary>
/// <param name="Location">The folder of the library, as <c>/Library/VirtualFolders</c> names it; <c>null</c> when the library has one.</param>
/// <param name="Path">The path of the file in the folder, with forward slashes.</param>
/// <param name="Length">The size of what is uploaded, in bytes: the file, or the zip of the TrueCache entry of a .strm file.</param>
/// <param name="WorkerId">The id the server registered the worker with.</param>
public sealed record HlsLibraryUploadStart(string? Location, string Path, long Length, Guid WorkerId);
