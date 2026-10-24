using System;
using System.Text.Json;
using Jellyfin.MediaEncoding.Hls.Published;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Published;

public class HlsProbeReaderTests
{
    [Fact]
    public void Parse_AttachedCover_IsNoVideo()
    {
        var probe = HlsProbeReader.Parse(
            """
            {"format":{"duration":"42.5"},"streams":[
              {"index":0,"codec_type":"video","codec_name":"mjpeg","disposition":{"attached_pic":1}},
              {"index":1,"codec_type":"video","codec_name":"h264","width":1280,"height":720,"avg_frame_rate":"30000/1001","pix_fmt":"yuv420p","field_order":"progressive",
               "side_data_list":[{"side_data_type":"Display Matrix","rotation":-90}]},
              {"index":2,"codec_type":"audio","codec_name":"aac","channels":6,"sample_rate":"48000","tags":{"language":"jpn","title":"Main"},"disposition":{"default":1}},
              {"index":3,"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"eng"}}
            ]}
            """);

        Assert.Equal(1280, probe.Width);
        Assert.Equal(720, probe.Height);
        Assert.Equal(30000d / 1001, probe.FramesPerSecond);
        Assert.Equal(42.5, probe.DurationSeconds);
        Assert.Equal("h264", probe.VideoCodec);
        Assert.Equal(-90, probe.Rotation);
        Assert.Equal(1, probe.VideoIndex);
        Assert.Equal(new MediaBrowser.Model.Hls.HlsAudioTrack(2, "aac", 6, 48000, "jpn", "Main", true), Assert.Single(probe.Audio));
        Assert.Equal(new MediaBrowser.Model.Hls.HlsSubtitleTrack(3, "subrip", "eng", string.Empty, false), Assert.Single(probe.Subtitles));
    }

    [Fact]
    public void Parse_NoAverageFrameRate_TakesTheRealFrameRate()
        => Assert.Equal(24, HlsProbeReader.Parse(VideoJson(null, frameRate: "\"avg_frame_rate\":\"0/0\",\"r_frame_rate\":\"24/1\"")).FramesPerSecond);

    [Theory]
    [InlineData("{\"format\":{\"duration\":\"10\"},\"streams\":[{\"index\":0,\"codec_type\":\"audio\"}]}")]
    [InlineData("{\"format\":{\"duration\":\"0\"},\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"width\":720,\"height\":480,\"avg_frame_rate\":\"25/1\"}]}")]
    [InlineData("{\"format\":{\"duration\":\"10\"},\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"width\":0,\"height\":480,\"avg_frame_rate\":\"25/1\"}]}")]
    [InlineData("{\"format\":{\"duration\":\"10\"},\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"width\":720,\"height\":480,\"avg_frame_rate\":\"0/0\"}]}")]
    [InlineData("{\"format\":{\"duration\":\"Infinity\"},\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"width\":720,\"height\":480,\"avg_frame_rate\":\"25/1\"}]}")]
    public void Parse_NoUsableVideo_Throws(string json)
        => Assert.Throws<ArgumentException>(() => HlsProbeReader.Parse(json));

    [Fact]
    public void Parse_NotJson_Throws()
        => Assert.ThrowsAny<JsonException>(() => HlsProbeReader.Parse("not json"));

    [Theory]
    [InlineData("32:27", 32, 27)]
    [InlineData("1:1", 1, 1)]
    [InlineData("16:11", 16, 11)]
    [InlineData("0:1", 1, 1)]
    [InlineData("N/A", 1, 1)]
    [InlineData("", 1, 1)]
    [InlineData("4:0", 1, 1)]
    [InlineData("-4:3", 1, 1)]
    [InlineData("4:-3", 1, 1)]
    [InlineData("a:b", 1, 1)]
    [InlineData("4", 1, 1)]
    [InlineData("4:3:2", 1, 1)]
    [InlineData("1.5:1", 1, 1)]
    [InlineData(" 4:3", 1, 1)]
    [InlineData("99999999999:1", 1, 1)]
    public void Parse_SampleAspectRatio_MustBeTwoPositiveIntegers(string text, int numerator, int denominator)
    {
        var probe = HlsProbeReader.Parse(VideoJson(text));

        Assert.Equal(numerator, probe.SarNumerator);
        Assert.Equal(denominator, probe.SarDenominator);
    }

    [Fact]
    public void Parse_NoSampleAspectRatio_IsSquarePixels()
    {
        var probe = HlsProbeReader.Parse(VideoJson(null));

        Assert.Equal(1, probe.SarNumerator);
        Assert.Equal(1, probe.SarDenominator);
    }

    [Fact]
    public void Parse_ColorSpace_IsRead()
        => Assert.Equal("bt470bg", HlsProbeReader.Parse(VideoJson(null, frameRate: "\"avg_frame_rate\":\"25/1\",\"color_space\":\"bt470bg\"")).ColorSpace);

    [Fact]
    public void Parse_NoColorSpace_IsEmpty()
        => Assert.Equal(string.Empty, HlsProbeReader.Parse(VideoJson(null)).ColorSpace);

    [Fact]
    public void NormalizeSha256_UpperCase_IsLowered()
        => Assert.Equal(new string('a', 64), HlsProbeReader.NormalizeSha256(new string('A', 64)));

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void NormalizeSha256_NoHash_Throws(string sha)
        => Assert.Throws<ArgumentException>(() => HlsProbeReader.NormalizeSha256(sha));

    private static string VideoJson(string? sampleAspectRatio, string frameRate = "\"avg_frame_rate\":\"25/1\"")
        => "{\"format\":{\"duration\":\"10\"},\"streams\":[{\"index\":0,\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":720,\"height\":480," + frameRate
            + (sampleAspectRatio is null ? string.Empty : ",\"sample_aspect_ratio\":" + JsonSerializer.Serialize(sampleAspectRatio)) + "}]}";
}
