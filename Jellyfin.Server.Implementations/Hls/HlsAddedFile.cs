using System;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A file a worker wrote into a library folder as a media content it added.
/// </summary>
/// <param name="Sha256">The hash of the content.</param>
/// <param name="ContentLength">The size of the content, in bytes.</param>
/// <param name="Length">The size of the file written, in bytes.</param>
/// <param name="DateCreated">When the file was written.</param>
public sealed record HlsAddedFile(string Sha256, long ContentLength, long Length, DateTime DateCreated);
