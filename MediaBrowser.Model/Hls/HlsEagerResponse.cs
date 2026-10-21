namespace MediaBrowser.Model.Hls;

/// <summary>
/// The job a worker adding media started, or that nothing is left to encode.
/// </summary>
/// <param name="Done">Whether nothing is left to encode, so no job was started.</param>
/// <param name="Lease">The lease of the job, unless done.</param>
/// <param name="Plan">The renditions to encode, unless done.</param>
public sealed record HlsEagerResponse(bool Done, HlsWorkLease? Lease, HlsEncodePlan? Plan);
