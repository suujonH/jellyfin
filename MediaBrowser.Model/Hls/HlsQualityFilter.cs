using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// The video qualities a worker does not encode for a library.
/// </summary>
/// <param name="LibraryId">The library.</param>
/// <param name="Excluded">The heights of the qualities, such as 720.</param>
public sealed record HlsQualityFilter(Guid LibraryId, IReadOnlyList<int>? Excluded);
