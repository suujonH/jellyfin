using System.Collections.Generic;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// A worker reporting the renditions it encoded and uploaded.
/// </summary>
/// <param name="LeaseToken">The token of the lease.</param>
/// <param name="Renditions">Every rendition of the plan.</param>
public sealed record HlsCompletion(string LeaseToken, IReadOnlyList<HlsRenditionOutput> Renditions);
