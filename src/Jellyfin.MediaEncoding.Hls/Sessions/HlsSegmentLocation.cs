namespace Jellyfin.MediaEncoding.Hls.Sessions;

/// <summary>
/// Where a plan segment lies in the list of a run.
/// </summary>
/// <param name="State">What the run has produced of the segment.</param>
/// <param name="FirstEntry">The index of the first list entry of the segment, or -1 when it has none.</param>
/// <param name="EntryCount">The number of list entries of the segment so far.</param>
internal readonly record struct HlsSegmentLocation(HlsSegmentState State, int FirstEntry, int EntryCount);
