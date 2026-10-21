using System;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A library file as the published index holds it.
/// </summary>
/// <param name="ItemId">The item.</param>
/// <param name="LibraryId">The library.</param>
/// <param name="Path">The path of the file, or the target of a <c>.strm</c> file.</param>
/// <param name="Sha256">The hash of its content, once identified.</param>
public sealed record HlsIndexedSource(Guid ItemId, Guid LibraryId, string Path, string? Sha256);
