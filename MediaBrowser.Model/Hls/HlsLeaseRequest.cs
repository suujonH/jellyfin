namespace MediaBrowser.Model.Hls;

/// <summary>
/// A request about a job that only shows the lease.
/// </summary>
/// <param name="LeaseToken">The token of the lease.</param>
public sealed record HlsLeaseRequest(string LeaseToken);
