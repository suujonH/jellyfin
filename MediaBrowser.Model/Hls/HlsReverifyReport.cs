using System;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// The result of verifying published renditions again.
/// </summary>
/// <param name="Date">When it ended.</param>
/// <param name="Checked">How many renditions were checked.</param>
/// <param name="Failed">How many failed and were retired, to be encoded again.</param>
/// <param name="Aborted">Why it stopped before it was done, such as an HLS store that cannot be trusted, otherwise <c>null</c>.</param>
public sealed record HlsReverifyReport(DateTime Date, int Checked, int Failed, string? Aborted = null);
