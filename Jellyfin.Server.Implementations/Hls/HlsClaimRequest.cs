using System;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A worker asking for a job.
/// </summary>
/// <param name="Worker">The name of the worker.</param>
/// <param name="Lease">How long the lease lasts.</param>
/// <param name="IsLibraryEligible">Whether pre-encoded HLS is enabled for a library.</param>
/// <param name="Now">The current time.</param>
public sealed record HlsClaimRequest(string Worker, TimeSpan Lease, Func<Guid, bool> IsLibraryEligible, DateTime Now);
