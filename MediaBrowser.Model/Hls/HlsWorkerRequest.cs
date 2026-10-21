namespace MediaBrowser.Model.Hls;

/// <summary>
/// A worker asking for work.
/// </summary>
/// <param name="Worker">The name of the worker, 1 to 128 characters.</param>
public sealed record HlsWorkerRequest(string Worker);
