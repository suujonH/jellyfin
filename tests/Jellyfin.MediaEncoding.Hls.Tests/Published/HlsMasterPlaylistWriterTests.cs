using System;
using System.Globalization;
using System.Linq;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Model.Hls;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Published;

// Ported from the DistributedHls plugin; tracks without a title are named in English.
public class HlsMasterPlaylistWriterTests
{
    private const string Head = "#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-INDEPENDENT-SEGMENTS\n";
    private const string EnglishLine = "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English [3]\",LANGUAGE=\"en\",DEFAULT=NO,AUTOSELECT=YES,FORCED=NO,URI=\"r/subtitles/3/index.m3u8\"";
    private const string ChineseLine = "#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Subtitles 4 [4]\",LANGUAGE=\"zh\",DEFAULT=NO,AUTOSELECT=YES,FORCED=NO,URI=\"r/subtitles/4/index.m3u8\"";
    private const string Variant720 = "#EXT-X-STREAM-INF:BANDWIDTH=2992000,AVERAGE-BANDWIDTH=2292000,RESOLUTION=1280x720,FRAME-RATE=29.97,CODECS=\"avc1.64001f,mp4a.40.2\",AUDIO=\"audio\",SUBTITLES=\"subs\"";
    private const string Variant1080 = "#EXT-X-STREAM-INF:BANDWIDTH=5192000,AVERAGE-BANDWIDTH=4192000,RESOLUTION=1920x1080,FRAME-RATE=29.97,CODECS=\"avc1.640028,mp4a.40.2\",AUDIO=\"audio\",SUBTITLES=\"subs\"";

    private static readonly HlsPublishedRendition _video1080 = Video(1080, 1920, 1080, 5_000_000, 4_000_000, "avc1.640028");
    private static readonly HlsPublishedRendition _video720 = Video(720, 1280, 720, 2_800_000, 2_100_000, "avc1.64001f");
    private static readonly HlsPublishedRendition _japanese = Audio(1, "jpn", string.Empty, 128_000);
    private static readonly HlsPublishedRendition _commentary = Audio(2, "eng", "Commentary", 192_000, isDefault: true);
    private static readonly HlsPublishedRendition _english = Subtitle(3, "eng", "English");
    private static readonly HlsPublishedRendition _chinese = Subtitle(4, "chi", string.Empty);

    [Fact]
    public void Write_Automatic_ListsEveryRenditionWithRelativeUris()
    {
        Assert.Equal(
            Head + Lines(
                JapaneseLine(false),
                CommentaryLine(true),
                EnglishLine,
                ChineseLine,
                // Variants are listed by ascending video bandwidth; BANDWIDTH adds the largest audio bandwidth (192000).
                Variant720,
                "r/video/720/index.m3u8",
                Variant1080,
                "r/video/1080/index.m3u8"),
            HlsMasterPlaylistWriter.Write(Everything(), 0, null));
    }

    [Fact]
    public void Write_Quality_KeepsOnlyThatVideoRendition()
    {
        Assert.Equal(
            Head + Lines(JapaneseLine(false), CommentaryLine(true), EnglishLine, ChineseLine, Variant720, "r/video/720/index.m3u8"),
            HlsMasterPlaylistWriter.Write(Everything(), 720, null));
    }

    [Fact]
    public void Write_AudioTrack_KeepsOnlyThatTrackAndMakesItTheDefault()
    {
        // Track 1 is not marked default and has the smaller bandwidth: it becomes the only and default track, and BANDWIDTH adds 128000.
        Assert.Equal(
            Head + Lines(
                JapaneseLine(true),
                EnglishLine,
                ChineseLine,
                "#EXT-X-STREAM-INF:BANDWIDTH=2928000,AVERAGE-BANDWIDTH=2228000,RESOLUTION=1280x720,FRAME-RATE=29.97,CODECS=\"avc1.64001f,mp4a.40.2\",AUDIO=\"audio\",SUBTITLES=\"subs\"",
                "r/video/720/index.m3u8",
                "#EXT-X-STREAM-INF:BANDWIDTH=5128000,AVERAGE-BANDWIDTH=4128000,RESOLUTION=1920x1080,FRAME-RATE=29.97,CODECS=\"avc1.640028,mp4a.40.2\",AUDIO=\"audio\",SUBTITLES=\"subs\"",
                "r/video/1080/index.m3u8"),
            HlsMasterPlaylistWriter.Write(Everything(), 0, 1));
    }

    [Fact]
    public void Write_QualityAndAudioTrack_Combine()
    {
        Assert.Equal(
            Head + Lines(CommentaryLine(true), EnglishLine, ChineseLine, Variant1080, "r/video/1080/index.m3u8"),
            HlsMasterPlaylistWriter.Write(Everything(), 1080, 2));
    }

    [Fact]
    public void Write_NoTrackMarkedDefault_MakesTheFirstOneTheDefault()
    {
        var text = HlsMasterPlaylistWriter.Write(Media([_video720], [_japanese, Audio(2, "eng", "Main", 192_000)], []), 0, null);
        var tracks = text.Split('\n').Where(l => l.StartsWith("#EXT-X-MEDIA:TYPE=AUDIO", StringComparison.Ordinal)).ToArray();
        Assert.Equal(2, tracks.Length);
        Assert.Contains("DEFAULT=YES", tracks[0], StringComparison.Ordinal);
        Assert.Contains("DEFAULT=NO", tracks[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Write_NoAudio_HasNoAudioGroupAndNoAudioCodec()
    {
        Assert.Equal(
            Head + Lines(
                EnglishLine,
                "#EXT-X-STREAM-INF:BANDWIDTH=2800000,AVERAGE-BANDWIDTH=2100000,RESOLUTION=1280x720,FRAME-RATE=29.97,CODECS=\"avc1.64001f\",SUBTITLES=\"subs\"",
                "r/video/720/index.m3u8"),
            HlsMasterPlaylistWriter.Write(Media([_video720], [], [_english]), 0, null));
    }

    [Fact]
    public void Write_NoSubtitles_HasNoSubtitleGroup()
    {
        Assert.Equal(
            Head + Lines(
                CommentaryLine(true),
                "#EXT-X-STREAM-INF:BANDWIDTH=2992000,AVERAGE-BANDWIDTH=2292000,RESOLUTION=1280x720,FRAME-RATE=29.97,CODECS=\"avc1.64001f,mp4a.40.2\",AUDIO=\"audio\"",
                "r/video/720/index.m3u8"),
            HlsMasterPlaylistWriter.Write(Media([_video720], [_commentary], []), 0, null));
    }

    [Fact]
    public void Write_UnpublishedSelections_AreLeftOut()
    {
        // The playback service checks the quality and the audio track; the writer only leaves out what is not there.
        var noVideo = HlsMasterPlaylistWriter.Write(Everything(), 480, null);
        Assert.DoesNotContain("#EXT-X-STREAM-INF", noVideo, StringComparison.Ordinal);
        Assert.DoesNotContain("r/video", noVideo, StringComparison.Ordinal);
        var noAudio = HlsMasterPlaylistWriter.Write(Everything(), 0, 9);
        Assert.DoesNotContain("TYPE=AUDIO", noAudio, StringComparison.Ordinal);
        Assert.DoesNotContain("mp4a", noAudio, StringComparison.Ordinal);
        Assert.Contains("r/video/1080/index.m3u8", noAudio, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_Names_AreQuotedSoTheyCannotBreakTheAttributeList()
    {
        var text = HlsMasterPlaylistWriter.Write(Media([_video720], [Audio(5, "eng", "He said \"hi\"\r\nnext\nline", 128_000)], []), 0, null);
        Assert.Contains("NAME=\"He said 'hi'  next line [5]\",", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', text);
        Assert.Single(text.Split('\n'), l => l.StartsWith("#EXT-X-MEDIA:TYPE=AUDIO", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("eng", "en")]
    [InlineData("jpn", "ja")]
    [InlineData("zho", "zh")]
    [InlineData("chi", "zh")]
    [InlineData("fra", "fr")]
    [InlineData("fre", "fr")]
    [InlineData("deu", "de")]
    [InlineData("ger", "de")]
    [InlineData("spa", "es")]
    [InlineData("kor", "ko")]
    [InlineData("", "und")]
    [InlineData("por", "por")]
    [InlineData("en", "en")]
    public void Language_Code_IsMappedToATwoLetterTag(string code, string expected)
        => Assert.Equal(expected, HlsMasterPlaylistWriter.Language(code));

    [Fact]
    public void Write_Output_UsesLineFeedsAndEndsWithOne()
    {
        var text = HlsMasterPlaylistWriter.Write(Everything(), 0, null);
        Assert.DoesNotContain('\r', text);
        Assert.EndsWith("r/video/1080/index.m3u8\n", text, StringComparison.Ordinal);
        Assert.StartsWith(Head, text, StringComparison.Ordinal);
    }

    [Fact]
    public void Write_Numbers_DoNotDependOnTheCurrentCulture()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Contains("FRAME-RATE=29.97,", HlsMasterPlaylistWriter.Write(Everything(), 0, null), StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [InlineData(0, null, "master")]
    [InlineData(720, null, "720p")]
    [InlineData(1080, 2, "1080p-a2")]
    [InlineData(0, 1, "master-a1")]
    [InlineData(720, 9, "720p")]
    public void GetPlaylistName_AudioTrack_IsNamedOnlyWhenPublished(int quality, int? audio, string expected)
        => Assert.Equal(expected, HlsMasterPlaylistWriter.GetPlaylistName(Everything(), quality, audio));

    [Theory]
    [InlineData("master", 0, null)]
    [InlineData("720p", 720, null)]
    [InlineData("1080p-a2", 1080, 2)]
    [InlineData("master-a12", 0, 12)]
    [InlineData("2160p-a999", 2160, 999)]
    public void TryParsePlaylistName_Name_IsRead(string name, int quality, int? audio)
    {
        Assert.True(HlsMasterPlaylistWriter.TryParsePlaylistName(name, out var parsedQuality, out var parsedAudio));
        Assert.Equal((quality, audio), (parsedQuality, parsedAudio));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Master")]
    [InlineData("master\n")]
    [InlineData("72p")]
    [InlineData("12345p")]
    [InlineData("720")]
    [InlineData("720p-a")]
    [InlineData("720p-a1234")]
    [InlineData("720p-a1-a2")]
    [InlineData("٧٢٠p")]
    [InlineData("../master")]
    public void TryParsePlaylistName_OtherName_IsNotAPlaylist(string? name)
    {
        Assert.False(HlsMasterPlaylistWriter.TryParsePlaylistName(name, out var quality, out var audio));
        Assert.Equal((0, (int?)null), (quality, audio));
    }

    [Theory]
    [InlineData("video", HlsRenditionKind.Video)]
    [InlineData("audio", HlsRenditionKind.Audio)]
    [InlineData("subtitles", HlsRenditionKind.Subtitle)]
    public void KindName_IsThatOfTheUris(string name, HlsRenditionKind kind)
    {
        Assert.Equal(name, HlsMasterPlaylistWriter.GetKindName(kind));
        Assert.True(HlsMasterPlaylistWriter.TryParseKindName(name, out var parsed));
        Assert.Equal(kind, parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("preview")]
    [InlineData("Video")]
    [InlineData("subtitle")]
    public void TryParseKindName_PreviewOrOtherName_IsNotListed(string? name)
    {
        Assert.False(HlsMasterPlaylistWriter.TryParseKindName(name, out _));
        Assert.Null(HlsMasterPlaylistWriter.GetKindName(HlsRenditionKind.Preview));
    }

    [Fact]
    public void Find_Rendition_IsFoundByQualityOrStreamIndex()
    {
        var media = Everything();
        Assert.Same(_video720, media.Find(HlsRenditionKind.Video, 720));
        Assert.Same(_commentary, media.Find(HlsRenditionKind.Audio, 2));
        Assert.Same(_chinese, media.Find(HlsRenditionKind.Subtitle, 4));
        Assert.Null(media.Find(HlsRenditionKind.Video, 480));
        Assert.Null(media.Find(HlsRenditionKind.Audio, 3));
        Assert.Null(media.Find(HlsRenditionKind.Preview, 0));
    }

    private static HlsPublishedRendition Video(int quality, int width, int height, int bandwidth, int average, string codec)
        => new(
            "v" + quality.ToString(CultureInfo.InvariantCulture),
            "/hls",
            "m/video/" + quality.ToString(CultureInfo.InvariantCulture) + "/t",
            new HlsRenditionSummary(Quality: quality, Width: width, Height: height, FramesPerSecond: 30000d / 1001, Bandwidth: bandwidth, AverageBandwidth: average, Codec: codec));

    private static HlsPublishedRendition Audio(int index, string language, string title, int bandwidth, bool isDefault = false)
        => new(
            "a" + index.ToString(CultureInfo.InvariantCulture),
            "/hls",
            "m/audio/" + index.ToString(CultureInfo.InvariantCulture) + "/t",
            new HlsRenditionSummary(SourceIndex: index, Language: language, Title: title, Channels: 2, Bandwidth: bandwidth, Default: isDefault));

    private static HlsPublishedRendition Subtitle(int index, string language, string title)
        => new(
            "s" + index.ToString(CultureInfo.InvariantCulture),
            "/hls",
            "m/subtitles/" + index.ToString(CultureInfo.InvariantCulture) + "/t",
            new HlsRenditionSummary(SourceIndex: index, Language: language, Title: title));

    private static HlsMediaView Media(HlsPublishedRendition[] video, HlsPublishedRendition[] audio, HlsPublishedRendition[] subtitles)
        => new(new string('a', 64), new string('a', 32), 120, 0, audio.Length, video, audio, subtitles);

    private static HlsMediaView Everything() => Media([_video1080, _video720], [_japanese, _commentary], [_english, _chinese]);

    // Playlists are compared line by line: the expected text must not depend on the line endings of this source file.
    private static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";

    private static string JapaneseLine(bool isDefault)
        => "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"Audio 1 [1]\",LANGUAGE=\"ja\",DEFAULT=" + (isDefault ? "YES" : "NO") + ",AUTOSELECT=YES,CHANNELS=\"2\",URI=\"r/audio/1/index.m3u8\"";

    private static string CommentaryLine(bool isDefault)
        => "#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"Commentary [2]\",LANGUAGE=\"en\",DEFAULT=" + (isDefault ? "YES" : "NO") + ",AUTOSELECT=YES,CHANNELS=\"2\",URI=\"r/audio/2/index.m3u8\"";
}
