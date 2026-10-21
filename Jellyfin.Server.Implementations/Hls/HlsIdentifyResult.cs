using MediaBrowser.Model.Hls;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// What identifying the file of a job led to.
/// </summary>
/// <param name="Outcome">The outcome.</param>
/// <param name="Plan">The plan, when the job encodes.</param>
/// <param name="KeptStoredProbe">Whether the content was known with another probe, which was kept.</param>
public sealed record HlsIdentifyResult(HlsIdentifyOutcome Outcome, HlsEncodePlan? Plan = null, bool KeptStoredProbe = false);
