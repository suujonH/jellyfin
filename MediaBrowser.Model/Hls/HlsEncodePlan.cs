using System.Collections.Generic;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// What a worker encodes for a job.
/// </summary>
/// <param name="Sha256">The hash of the content.</param>
/// <param name="MediaKey">The directory of the content in the HLS store.</param>
/// <param name="Profile">The version of the encoding parameters.</param>
/// <param name="Probe">The probe the server keeps for the content.</param>
/// <param name="SegmentSeconds">The length of a segment.</param>
/// <param name="KeyframeSeconds">The interval of the forced keyframes.</param>
/// <param name="AudioSampleRate">The sample rate of the audio renditions.</param>
/// <param name="ExpectedSegments">How many segments a video or audio rendition is expected to have; one more or one less is accepted.</param>
/// <param name="Renditions">The renditions to encode.</param>
public sealed record HlsEncodePlan(
    string Sha256,
    string MediaKey,
    string Profile,
    HlsProbe Probe,
    double SegmentSeconds,
    double KeyframeSeconds,
    int AudioSampleRate,
    int ExpectedSegments,
    IReadOnlyList<HlsPlannedRendition> Renditions);
