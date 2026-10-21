using System.Collections.Generic;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// An encoded rendition a worker reports.
/// </summary>
/// <param name="Key">The key of the rendition.</param>
/// <param name="Summary">What the worker found about the rendition.</param>
/// <param name="Files">The files of the rendition.</param>
/// <param name="SegmentDurations">The duration of every segment, in seconds; empty for subtitles and previews.</param>
public sealed record HlsRenditionOutput(string Key, HlsRenditionSummary Summary, IReadOnlyList<HlsOutputFile> Files, IReadOnlyList<double> SegmentDurations);
