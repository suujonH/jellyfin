namespace MediaBrowser.Model.Hls;

/// <summary>
/// What a job encodes once its file is identified.
/// </summary>
/// <param name="Done">Whether nothing is left to encode, so the job is complete.</param>
/// <param name="Plan">The renditions to encode, unless the job is done.</param>
public sealed record HlsIdentifyResponse(bool Done, HlsEncodePlan? Plan);
