using System.Collections.Generic;
using MediaBrowser.Model.Hls;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A reported rendition waiting to be verified.
/// </summary>
/// <param name="Key">The key of the rendition.</param>
/// <param name="RelativeDirectory">The directory, from the plan.</param>
/// <param name="Files">The reported files.</param>
public sealed record HlsVerifyingOutput(string Key, string RelativeDirectory, IReadOnlyList<HlsOutputFile> Files);
