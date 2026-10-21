namespace MediaBrowser.Model.Hls;

/// <summary>
/// A worker asking whether it may upload to a root of an HLS store.
/// </summary>
/// <param name="Base">The root: <c>s3://bucket/prefix</c>, or an absolute path on the server.</param>
public sealed record HlsBaseRequest(string Base);
