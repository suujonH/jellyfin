namespace Jellyfin.MediaEncoding.Hls.Tests.Ffmpeg;

/// <summary>
/// One packet as ffprobe reports it, compared between runs to prove that two files carry the same media.
/// </summary>
/// <param name="StreamIndex">The stream index inside the probed file.</param>
/// <param name="Pts">The presentation time as ffprobe prints it, so equal timestamps compare equal.</param>
/// <param name="Size">The packet size in bytes.</param>
/// <param name="IsKeyframe">Whether the packet is a keyframe.</param>
public sealed record ProbePacket(int StreamIndex, string Pts, int Size, bool IsKeyframe);
