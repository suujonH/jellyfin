using System.Collections.Generic;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// A worker asking where to upload files of a rendition.
/// </summary>
/// <param name="LeaseToken">The token of the lease.</param>
/// <param name="Key">The key of the rendition in the plan.</param>
/// <param name="Files">The files, by name in the directory of the rendition; at most 500.</param>
public sealed record HlsUploadRequest(string LeaseToken, string Key, IReadOnlyList<HlsOutputFile> Files);
