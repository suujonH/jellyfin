using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Ffmpeg;

/// <summary>
/// The cuts of a pre-encode: one run from the start, cut by the hls muxer, with every rendition encoded from one decode.
/// </summary>
public sealed class PreEncodeCutTests : IClassFixture<FfmpegFixture>
{
    private const double SegmentLength = 6;

    private const double Tolerance = 0.002;

    // An AAC frame at 48 kHz.
    private const double AudioPacket = 1024d / 48000;

    private readonly FfmpegFixture _ffmpeg;

    public PreEncodeCutTests(FfmpegFixture ffmpeg)
    {
        _ffmpeg = ffmpeg;
    }

    [Fact]
    public async Task OneDecode_Renditions_ShareTheCuts()
    {
        _ffmpeg.SkipIfUnavailable();
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = _ffmpeg.CreateRunDirectory("pre-encode");
        foreach (var rendition in new[] { "v0", "v1", "a0" })
        {
            Directory.CreateDirectory(Path.Combine(directory, rendition));
        }

        // A keyframe every 2 s and closed GOPs in every rendition; the muxer cuts at the first keyframe from each 6 s.
        string[] video = ["-preset", "veryfast", "-bf", "2", "-sc_threshold", "0", "-g", "480", "-force_key_frames", "expr:gte(t,n_forced*2)", "-flags", "+cgop"];
        await _ffmpeg.FfmpegAsync(
            directory,
            [
                "-i", _ffmpeg.Mkv, "-filter_complex", "[0:v:0]split=2[s0][s1];[s0]scale=-2:180[v0];[s1]scale=-2:90[v1]",
                "-map", "[v0]", "-c:v", "libx264", .. video, .. Hls("v0"),
                "-map", "[v1]", "-c:v", "libx264", .. video, .. Hls("v1"),
                "-map", "0:a:0", "-c:a", "aac", "-b:a", "96k", .. Hls("a0")
            ],
            cancellationToken);

        var v0 = ReadDurations(directory, "v0");
        var v1 = ReadDurations(directory, "v1");
        var a0 = ReadDurations(directory, "a0");

        // The video renditions cut at the same frames, each segment within a frame of the length but the last.
        Assert.Equal(v0, v1);
        Assert.Equal((int)Math.Ceiling(FfmpegFixture.SourceSeconds / SegmentLength), v0.Count);
        Assert.All(v0.SkipLast(1), d => Assert.InRange(d, SegmentLength - FfmpegFixture.FrameDuration - Tolerance, SegmentLength + FfmpegFixture.FrameDuration + Tolerance));

        // Audio is cut at the first packet from each boundary, so within a packet; a last packet past the final cut may add a segment.
        Assert.InRange(a0.Count, v0.Count, v0.Count + 1);
        Assert.All(a0.Take(v0.Count - 1), d => Assert.InRange(d, SegmentLength - AudioPacket - Tolerance, SegmentLength + AudioPacket + Tolerance));
    }

    private static string[] Hls(string rendition) =>
    [
        "-f", "hls", "-hls_time", FfmpegFixture.FormatSeconds(SegmentLength), "-hls_playlist_type", "vod", "-hls_list_size", "0",
        "-hls_segment_type", "fmp4", "-hls_fmp4_init_filename", "init.mp4", "-hls_flags", "+independent_segments+temp_file",
        "-hls_segment_options", "movflags=+skip_sidx", "-hls_segment_filename", $"{rendition}/segment-%06d.m4s", $"{rendition}/index.m3u8"
    ];

    private static List<double> ReadDurations(string directory, string rendition)
        => File.ReadAllLines(Path.Combine(directory, rendition, "index.m3u8"))
            .Where(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal))
            .Select(l => FfmpegFixture.ParseSeconds(l["#EXTINF:".Length..].TrimEnd(',')))
            .ToList();
}
