using MediaBrowser.Model.Hls;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A recorded library file.
/// </summary>
/// <param name="File">The file.</param>
/// <param name="Sha256">The hash of its content, once identified.</param>
public sealed record HlsSourceRecord(HlsSourceFile File, string? Sha256);
