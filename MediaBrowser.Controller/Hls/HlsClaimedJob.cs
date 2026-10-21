using MediaBrowser.Model.Hls;

namespace MediaBrowser.Controller.Hls;

/// <summary>
/// A job a worker claimed.
/// </summary>
/// <param name="Lease">The lease.</param>
/// <param name="Source">The library file the job reads.</param>
public sealed record HlsClaimedJob(HlsWorkLease Lease, HlsSourceFile Source);
