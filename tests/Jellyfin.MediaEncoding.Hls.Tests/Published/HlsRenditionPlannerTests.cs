using System.Linq;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Model.Hls;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Published;

public class HlsRenditionPlannerTests
{
    [Fact]
    public void GetDesired_Probe_PlansVideoThenAudioThenTextSubtitlesThenThePreview()
    {
        var probe = Probe(audio: [Audio(1, 6), Audio(2, 1)], subtitles: [Subtitle(3, "subrip"), Subtitle(4, "hdmv_pgs_subtitle"), Subtitle(5, "ass")]);

        Assert.Equal(
            [
                new HlsRenditionSpec("v1080", HlsRenditionKind.Video, new HlsVideoQuality(1080, 1080, 1920, 25), -1, 0),
                new HlsRenditionSpec("v720", HlsRenditionKind.Video, new HlsVideoQuality(720, 720, 1280, 25), -1, 0),
                new HlsRenditionSpec("a1", HlsRenditionKind.Audio, null, 1, 2),
                new HlsRenditionSpec("a2", HlsRenditionKind.Audio, null, 2, 1),
                new HlsRenditionSpec("s3", HlsRenditionKind.Subtitle, null, 3, 0),
                new HlsRenditionSpec("s5", HlsRenditionKind.Subtitle, null, 5, 0),
                new HlsRenditionSpec("preview", HlsRenditionKind.Preview, null, -1, 0)
            ],
            HlsRenditionPlanner.GetDesired(probe, [1080, 720]));
    }

    [Fact]
    public void GetDesired_VideoQualities_ComeFromTheLadder()
    {
        var probe = Probe(1920, 800);

        var specs = HlsRenditionPlanner.GetDesired(probe, [720, 1080, 2160]);

        Assert.Equal(HlsEncodingLadder.Select(probe, [720, 1080, 2160]), specs.Where(s => s.Kind == HlsRenditionKind.Video).Select(s => s.Video!));
        Assert.Equal(["v1080", "v720", "preview"], specs.Select(s => s.Key));
    }

    [Fact]
    public void GetDesired_NoQualityFits_PlansNothing()
    {
        // 1280x720 would have to be enlarged for 1080: without a video rendition audio, subtitles and the preview are not encoded either.
        var probe = Probe(1280, 720, [Audio(1, 2)], [Subtitle(2, "subrip")]);

        Assert.Empty(HlsRenditionPlanner.GetDesired(probe, [1080]));
        Assert.Empty(HlsRenditionPlanner.GetDesired(probe, []));
    }

    [Fact]
    public void GetDesired_NoAudioOrSubtitles_PlansVideoAndThePreview()
        => Assert.Equal(["v1080", "preview"], HlsRenditionPlanner.GetDesired(Probe(), [1080]).Select(s => s.Key));

    [Theory]
    [InlineData("subrip", true)]
    [InlineData("srt", true)]
    [InlineData("ass", true)]
    [InlineData("ssa", true)]
    [InlineData("webvtt", true)]
    [InlineData("mov_text", true)]
    [InlineData("text", true)]
    [InlineData("hdmv_pgs_subtitle", false)]
    [InlineData("dvd_subtitle", false)]
    [InlineData("dvb_subtitle", false)]
    [InlineData("xsub", false)]
    [InlineData("", false)]
    public void GetDesired_Subtitle_IsPlannedOnlyAsText(string codec, bool planned)
        => Assert.Equal(planned, HlsRenditionPlanner.GetDesired(Probe(subtitles: [Subtitle(7, codec)]), [1080]).Any(s => s.Key == "s7"));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(6, 2)]
    [InlineData(8, 2)]
    public void GetDesired_AudioChannels_AreClampedToOneOrTwo(int channels, int expected)
        => Assert.Equal(expected, HlsRenditionPlanner.GetDesired(Probe(audio: [Audio(1, channels)]), [1080]).Single(s => s.Kind == HlsRenditionKind.Audio).Channels);

    [Fact]
    public void GetDesired_AudioAndSubtitleKeys_UseTheStreamIndex()
    {
        var specs = HlsRenditionPlanner.GetDesired(Probe(audio: [Audio(4, 2), Audio(9, 2)], subtitles: [Subtitle(12, "srt")]), [1080]);

        Assert.Equal(["v1080", "a4", "a9", "s12", "preview"], specs.Select(s => s.Key));
        Assert.Equal([4, 9], specs.Where(s => s.Kind == HlsRenditionKind.Audio).Select(s => s.SourceIndex));
    }

    [Fact]
    public void Version_IsPinned()
        // Changing an encoding parameter or a ladder rule must change the version: published renditions are encoded again.
        => Assert.Equal("h264-aac-sdr-v3", HlsEncodingProfile.Version);

    [Theory]
    [InlineData(0.5, 1)]
    [InlineData(6, 1)]
    [InlineData(6.001, 2)]
    [InlineData(7200, 1200)]
    [InlineData(1e300, int.MaxValue)]
    public void GetExpectedSegments_Duration_IsTheNumberOfStartedSegments(double duration, int expected)
        => Assert.Equal(expected, HlsEncodingProfile.GetExpectedSegments(duration));

    private static HlsProbe Probe(int width = 1920, int height = 1080, HlsAudioTrack[]? audio = null, HlsSubtitleTrack[]? subtitles = null)
        => new(width, height, 25, 60, "h264", "yuv420p", "bt709", "progressive", 0, audio ?? [], subtitles ?? []);

    private static HlsAudioTrack Audio(int index, int channels) => new(index, "aac", channels, 48000, "eng", string.Empty, false);

    private static HlsSubtitleTrack Subtitle(int index, string codec) => new(index, codec, "eng", string.Empty, false);
}
