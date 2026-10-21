using System.Collections.Generic;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// The segments of a published video or audio rendition, which its media playlist lists.
/// </summary>
/// <param name="RelativeDirectory">The published directory.</param>
/// <param name="Durations">The duration of every segment, in seconds, as the worker measured it.</param>
public sealed record HlsPublishedSegments(string RelativeDirectory, IReadOnlyList<double> Durations);
