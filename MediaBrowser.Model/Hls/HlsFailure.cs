namespace MediaBrowser.Model.Hls;

/// <summary>
/// A worker giving up a job.
/// </summary>
/// <param name="LeaseToken">The token of the lease.</param>
/// <param name="Error">Why; only the first 2048 characters are kept.</param>
public sealed record HlsFailure(string LeaseToken, string Error);
