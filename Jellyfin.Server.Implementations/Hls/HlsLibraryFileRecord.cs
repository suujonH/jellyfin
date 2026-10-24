using System;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A file a worker wrote into a library folder.
/// </summary>
/// <param name="LibraryId">The library.</param>
/// <param name="Path">The full path of the file on the server.</param>
/// <param name="Length">The size of the file, in bytes.</param>
/// <param name="WorkerId">The worker.</param>
public sealed record HlsLibraryFileRecord(Guid LibraryId, string Path, long Length, Guid WorkerId);
