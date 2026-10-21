using System;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// The lease of a job a worker claimed.
/// </summary>
/// <param name="JobId">The job.</param>
/// <param name="LeaseToken">The token every later request about the job carries.</param>
/// <param name="LeaseSeconds">How long the lease lasts without a heartbeat.</param>
/// <param name="Attempt">The attempt of the job this is, from 1.</param>
public sealed record HlsWorkLease(Guid JobId, string LeaseToken, int LeaseSeconds, int Attempt);
