using System;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A file a worker wrote into a library folder.
/// </summary>
/// <param name="LibraryId">The library.</param>
/// <param name="Path">The full path of the file on the server.</param>
/// <param name="Length">The size of the file, in bytes.</param>
/// <param name="WorkerId">The worker.</param>
/// <param name="Sha256">The hash of the media content the file is; null for other files.</param>
/// <param name="ContentLength">The size of that content, in bytes.</param>
public sealed record HlsLibraryFileRecord(Guid LibraryId, string Path, long Length, Guid WorkerId, string? Sha256 = null, long? ContentLength = null);
