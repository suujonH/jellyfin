using System.Collections.Generic;
using MediaBrowser.Model.Hls;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A published rendition with the files its job reported.
/// </summary>
/// <param name="Sha256">The hash of the content.</param>
/// <param name="Key">The key of the rendition.</param>
/// <param name="Base">The root of the HLS store the directory is in.</param>
/// <param name="RelativeDirectory">The published directory.</param>
/// <param name="Files">The files.</param>
public sealed record HlsPublishedManifest(string Sha256, string Key, string Base, string RelativeDirectory, IReadOnlyList<HlsOutputFile> Files);
