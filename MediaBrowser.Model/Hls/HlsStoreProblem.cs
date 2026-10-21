namespace MediaBrowser.Model.Hls;

/// <summary>
/// A root of an HLS store that cannot be trusted.
/// </summary>
/// <param name="Base">The root: an absolute local path, or <c>s3://bucket/prefix</c>.</param>
/// <param name="Problem">Why it cannot be trusted.</param>
public sealed record HlsStoreProblem(string Base, string Problem);
