namespace Jellyfin.MediaEncoding.Hls.Plans;

/// <summary>
/// How a live HLS run starts so that its files reproduce the plan from a given segment.
/// </summary>
/// <param name="InputSeekTicks">The input <c>-ss</c> on the plan timeline, or 0 to read from the start.</param>
/// <param name="OutputSeekTicks">The output <c>-ss</c>, passed as <c>-output_ts_offset</c> as well to undo its shift of the timestamps; 0 for none.</param>
/// <param name="FirstFileStartTicks">
/// Where the first file of the run starts on the plan timeline, which the segment list does not record; null when the file
/// must be dropped, because its start is unknown and it carries audio from before its keyframe.
/// </param>
public readonly record struct HlsRunStart(long InputSeekTicks, long OutputSeekTicks, long? FirstFileStartTicks);
