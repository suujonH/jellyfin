namespace MediaBrowser.Model.Hls;

/// <summary>
/// The result of checking a root of an HLS store a worker reports.
/// </summary>
/// <param name="Success">Whether workers may upload to the root: the server can read it, and can trust it.</param>
/// <param name="HasMarker">Whether the root carries the marker that the store is the HLS store.</param>
/// <param name="Message">Why the root cannot be used, or <c>null</c>.</param>
public sealed record HlsStoreTestResult(bool Success, bool HasMarker, string? Message);
