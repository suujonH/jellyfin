using System;
using System.Collections.Generic;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A worker asking for a job.
/// </summary>
/// <param name="Worker">The name of the worker.</param>
/// <param name="WorkerId">The id of the worker.</param>
/// <param name="Lease">How long the lease lasts.</param>
/// <param name="IsLibraryEligible">Whether pre-encoded HLS is enabled for a library.</param>
/// <param name="Libraries">The libraries whose jobs the worker takes, or <c>null</c> for all of them.</param>
/// <param name="ExcludedItems">The items whose jobs the worker does not take.</param>
/// <param name="ExcludedQualities">The video qualities the worker does not encode, by library; a library not listed has none.</param>
/// <param name="Now">The current time.</param>
public sealed record HlsClaimRequest(
    string Worker,
    Guid WorkerId,
    TimeSpan Lease,
    Func<Guid, bool> IsLibraryEligible,
    IReadOnlySet<Guid>? Libraries,
    IReadOnlySet<Guid> ExcludedItems,
    IReadOnlyDictionary<Guid, IReadOnlySet<int>> ExcludedQualities,
    DateTime Now);
