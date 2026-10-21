namespace MediaBrowser.Model.Hls;

/// <summary>
/// The result of testing the HLS store.
/// </summary>
/// <param name="Success">Whether a test object could be written, read and deleted.</param>
/// <param name="HasMarker">Whether the root carries the marker that the store is the HLS store.</param>
/// <param name="Message">What went wrong.</param>
public sealed record HlsStoreTestResult(bool Success, bool HasMarker, string? Message);
