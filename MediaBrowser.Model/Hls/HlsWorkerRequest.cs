using System;
using System.Collections.Generic;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// A worker asking for work.
/// </summary>
/// <param name="Worker">The name of the worker, 1 to 128 characters.</param>
/// <param name="WorkerId">The id the server registered the worker with.</param>
/// <param name="Libraries">The libraries whose jobs the worker takes, or <c>null</c> for every pre-encoded library.</param>
/// <param name="ExcludedItems">The items whose jobs the worker does not take, as it cannot read their files.</param>
/// <param name="ExcludedQualities">The qualities the worker does not encode, by library; a library not listed has none excluded.</param>
public sealed record HlsWorkerRequest(
    string Worker,
    Guid WorkerId,
    IReadOnlyList<Guid>? Libraries = null,
    IReadOnlyList<Guid>? ExcludedItems = null,
    IReadOnlyList<HlsQualityFilter>? ExcludedQualities = null);
