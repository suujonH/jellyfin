namespace Jellyfin.MediaEncoding.Hls.Sessions;

/// <summary>
/// One line of the CSV list of the segment muxer: a GOP file and where it lies on the output timeline.
/// </summary>
/// <param name="FileName">The name of the file, without its directory.</param>
/// <param name="StartTicks">The start of the file; 0 for the first file of a run, whose start the muxer does not record.</param>
/// <param name="EndTicks">The end of the file, which is the start of the next one.</param>
internal readonly record struct HlsListEntry(string FileName, long StartTicks, long EndTicks);
