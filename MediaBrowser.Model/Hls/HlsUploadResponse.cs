using System.Collections.Generic;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// Where a worker uploads the files it asked about.
/// </summary>
/// <param name="Files">The addresses, in the order of the request.</param>
public sealed record HlsUploadResponse(IReadOnlyList<HlsUploadAddress> Files);
