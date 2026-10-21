namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// What starting a job for media being added led to.
/// </summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="Lease">The lease of the job, when one was started.</param>
/// <param name="KeptStoredProbe">Whether the content was known with another probe, which was kept.</param>
public sealed record HlsEagerJobResult(HlsEagerJobOutcome Outcome, HlsJobLease? Lease = null, bool KeptStoredProbe = false);
