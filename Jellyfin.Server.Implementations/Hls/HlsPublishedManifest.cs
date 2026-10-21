using System.Collections.Generic;
using MediaBrowser.Model.Hls;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A published rendition with the files its job reported.
/// </summary>
/// <param name="Sha256">The hash of the content.</param>
/// <param name="Key">The key of the rendition.</param>
/// <param name="RelativeDirectory">The published directory.</param>
/// <param name="Files">The files.</param>
public sealed record HlsPublishedManifest(string Sha256, string Key, string RelativeDirectory, IReadOnlyList<HlsOutputFile> Files);
