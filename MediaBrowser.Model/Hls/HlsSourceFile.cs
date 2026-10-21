using System;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// A library file that pre-encoded HLS is made from.
/// </summary>
/// <param name="ItemId">The id of the item.</param>
/// <param name="LibraryId">The id of the library the item is in.</param>
/// <param name="Path">The path of the file, or the target a <c>.strm</c> file names.</param>
/// <param name="Length">The size of the media, in bytes, or 0 when it is not known.</param>
/// <param name="DateModified">When the file was last modified, in UTC.</param>
public sealed record HlsSourceFile(Guid ItemId, Guid LibraryId, string Path, long Length, DateTime DateModified);
