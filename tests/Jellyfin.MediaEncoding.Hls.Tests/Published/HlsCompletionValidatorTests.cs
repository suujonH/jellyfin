using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Model.Hls;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Published;

public class HlsCompletionValidatorTests
{
    private const double Fps = 24000d / 1001;

    private static readonly HlsProbe _probe = new(1920, 1080, Fps, 20, "h264", "yuv420p", "bt709", "progressive", 0, [], []);

    // 20 seconds: 4 segments of 6 seconds expected, the last one 2 seconds long.
    private static readonly HlsEncodePlan _plan = new(
        new string('a', 64),
        new string('a', 32),
        HlsEncodingProfile.Version,
        _probe,
        6,
        2,
        48000,
        4,
        [
            new("v1080", HlsRenditionKind.Video, "m/video/1080/t", new HlsVideoQuality(1080, 1080, 1920, Fps), -1, 0),
            new("a1", HlsRenditionKind.Audio, "m/audio/1/t", null, 1, 2),
            new("s3", HlsRenditionKind.Subtitle, "m/subtitles/3/t", null, 3, 0),
            new("preview", HlsRenditionKind.Preview, "m/preview/t", null, -1, 0)
        ]);

    [Fact]
    public void Validate_OutputsOfThePlan_AreAccepted()
        => HlsCompletionValidator.Validate(_plan, Renditions());

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    public void Validate_OneSegmentMoreOrLess_IsAccepted(int count)
        => HlsCompletionValidator.Validate(_plan, With("v1080", r => r with { Files = Segments(count), SegmentDurations = Durations(count, 6.006, 1) }));

    [Fact]
    public void Validate_NoRenditions_IsRefused()
    {
        Refused([]);
        Assert.Throws<ArgumentException>(() => HlsCompletionValidator.Validate(_plan, null));
    }

    [Fact]
    public void Validate_MissingRendition_IsRefused()
        => Refused(Renditions().Where(r => r.Key != "a1").ToArray(), "a1");

    [Fact]
    public void Validate_RenditionOutsideThePlan_IsRefused()
        => Refused([.. Renditions(), new("v720", Renditions()[0].Summary, Segments(4), Durations(4, 6, 2))], "v720");

    [Fact]
    public void Validate_OtherKeyOfTheSameCount_IsRefused()
        => Refused(Renditions().Select(r => r.Key == "preview" ? r with { Key = "thumbnail" } : r).ToArray(), "thumbnail");

    [Fact]
    public void Validate_RenditionTwice_IsRefused()
        => Refused([.. Renditions(), Renditions()[3]], "preview");

    [Theory]
    [InlineData("v1080", "init.mp4")]
    [InlineData("a1", "init.mp4")]
    [InlineData("s3", "subtitle.vtt")]
    [InlineData("preview", "preview.jpg")]
    public void Validate_RequiredFileMissing_IsRefused(string key, string required)
        => Refused(With(key, r => r with { Files = r.Files.Where(f => f.Path != required).ToArray() }), key);

    [Theory]
    [InlineData("v1080", "index.m3u8")] // The server writes the playlists.
    [InlineData("v1080", "INIT.MP4")] // The server serves the names with this spelling.
    [InlineData("v1080", "../segment-000001.m4s")]
    [InlineData("v1080", "a/segment-000001.m4s")]
    [InlineData("v1080", "segment-000005.m4s")] // Two more than expected.
    [InlineData("v1080", "subtitle.vtt")]
    [InlineData("s3", "init.mp4")]
    [InlineData("preview", "preview.png")]
    [InlineData("preview", "")]
    public void Validate_FileNotAllowed_IsRefused(string key, string path)
        => Refused(With(key, r => r with { Files = [.. r.Files, new HlsOutputFile(path, 10)] }), key);

    [Fact]
    public void Validate_NoFiles_IsRefused()
    {
        Refused(With("preview", r => r with { Files = [] }), "preview");
        Refused(With("preview", r => r with { Files = null! }), "preview");
    }

    [Fact]
    public void Validate_FileTwiceOrWithANegativeLength_IsRefused()
    {
        Refused(With("preview", r => r with { Files = [new HlsOutputFile("preview.jpg", 1), new HlsOutputFile("preview.jpg", 1)] }), "preview.jpg");
        Refused(With("preview", r => r with { Files = [new HlsOutputFile("preview.jpg", -1)] }), "preview.jpg");
    }

    [Theory]
    [InlineData(new[] { 1, 2, 3, 4 })]
    [InlineData(new[] { 0, 1, 3, 4 })]
    [InlineData(new int[0])]
    public void Validate_SegmentsWithGaps_AreRefused(int[] indexes)
        => Refused(
            With("v1080", r => r with
            {
                Files = [new HlsOutputFile("init.mp4", 1), .. indexes.Select(i => new HlsOutputFile(HlsRenditionPaths.GetSegmentFile(i), 1))],
                SegmentDurations = Durations(indexes.Length, 6.006, 2)
            }),
            "v1080");

    [Fact]
    public void Validate_TwoSegmentsLess_IsRefused()
        => Refused(With("a1", r => r with { Files = Segments(2), SegmentDurations = Durations(2, 6, 2) }), "a1");

    [Fact]
    public void Validate_DurationsOfAnotherCount_AreRefused()
    {
        Refused(With("v1080", r => r with { SegmentDurations = Durations(3, 6, 2) }), "v1080");
        Refused(With("v1080", r => r with { SegmentDurations = null! }), "v1080");
        Refused(With("s3", r => r with { SegmentDurations = [20] }), "s3");
    }

    [Theory]
    [InlineData(6.05)] // More than a frame (0.0417 s) long.
    [InlineData(5.95)]
    [InlineData(0)]
    [InlineData(double.NaN)]
    public void Validate_VideoSegmentOffByMoreThanAFrame_IsRefused(double duration)
        => Refused(With("v1080", r => r with { SegmentDurations = [6.006, duration, 6.006, 2] }), "Segment 1");

    [Fact]
    public void Validate_VideoSegmentsWithinAFrame_AreAccepted()
        => HlsCompletionValidator.Validate(_plan, With("v1080", r => r with { SegmentDurations = [6.04, 5.96, 6.006, 6.04] }));

    [Fact]
    public void Validate_LastSegmentLongerThanASegment_IsRefused()
        => Refused(With("v1080", r => r with { SegmentDurations = [6, 6, 6, 6.1] }), "Segment 3");

    [Theory]
    [InlineData(6.02, true)] // Within an AAC frame (0.0213 s).
    [InlineData(6.03, false)]
    public void Validate_AudioSegment_IsWithinAnAacFrame(double duration, bool accepted)
    {
        var renditions = With("a1", r => r with { SegmentDurations = [duration, 6, 6, 2] });
        if (accepted)
        {
            HlsCompletionValidator.Validate(_plan, renditions);
        }
        else
        {
            Refused(renditions, "Segment 0");
        }
    }

    [Theory]
    [InlineData(1080, 1920, 1080, 5_000_000, 4_000_000, "avc1.640028", true)]
    [InlineData(720, 1920, 1080, 5_000_000, 4_000_000, "avc1.640028", false)]
    [InlineData(1080, 1918, 1080, 5_000_000, 4_000_000, "avc1.640028", false)]
    [InlineData(1080, 1920, 1080, 0, 4_000_000, "avc1.640028", false)]
    [InlineData(1080, 1920, 1080, 5_000_000, 0, "avc1.640028", false)]
    [InlineData(1080, 1920, 1080, 5_000_000, 4_000_000, "avc1.64002", false)]
    [InlineData(1080, 1920, 1080, 5_000_000, 4_000_000, "hvc1.640028", false)]
    [InlineData(1080, 1920, 1080, 5_000_000, 4_000_000, "avc1.64002g", false)]
    public void Validate_VideoSummary_MustAgreeWithThePlan(int quality, int width, int height, int bandwidth, int averageBandwidth, string codec, bool accepted)
    {
        var renditions = With("v1080", r => r with { Summary = new(quality, width, height, Fps, bandwidth, averageBandwidth, codec) });
        if (accepted)
        {
            HlsCompletionValidator.Validate(_plan, renditions);
        }
        else
        {
            Refused(renditions, "v1080");
        }
    }

    [Theory]
    [InlineData(1, 2, 192_000, true)]
    [InlineData(1, 1, 96_000, true)]
    [InlineData(2, 2, 192_000, false)]
    [InlineData(1, 3, 192_000, false)]
    [InlineData(1, 0, 192_000, false)]
    [InlineData(1, 2, 0, false)]
    public void Validate_AudioSummary_MustAgreeWithThePlan(int sourceIndex, int channels, int bandwidth, bool accepted)
    {
        var renditions = With("a1", r => r with { Summary = new(SourceIndex: sourceIndex, Channels: channels, Bandwidth: bandwidth) });
        if (accepted)
        {
            HlsCompletionValidator.Validate(_plan, renditions);
        }
        else
        {
            Refused(renditions, "a1");
        }
    }

    [Fact]
    public void Validate_SubtitleOfAnotherStream_IsRefused()
        => Refused(With("s3", r => r with { Summary = new(SourceIndex: 4) }), "s3");

    [Fact]
    public void Validate_NoSummary_IsRefused()
        => Refused(With("preview", r => r with { Summary = null! }), "preview");

    private static HlsRenditionOutput[] Renditions() =>
    [
        new("v1080", new HlsRenditionSummary(1080, 1920, 1080, Fps, 5_000_000, 4_000_000, "avc1.640028"), Segments(4), Durations(4, 6.006, 1.982)),
        new("a1", new HlsRenditionSummary(SourceIndex: 1, Language: "en", Channels: 2, Bandwidth: 192_000), Segments(4), Durations(4, 6.0, 2.0)),
        new("s3", new HlsRenditionSummary(SourceIndex: 3, Language: "en"), [new HlsOutputFile("subtitle.vtt", 100)], []),
        new("preview", new HlsRenditionSummary(), [new HlsOutputFile("preview.jpg", 100)], [])
    ];

    private static HlsOutputFile[] Segments(int count)
        => [new HlsOutputFile("init.mp4", 800), .. Enumerable.Range(0, count).Select(i => new HlsOutputFile(HlsRenditionPaths.GetSegmentFile(i), 1000))];

    private static double[] Durations(int count, double length, double last)
        => [.. Enumerable.Range(0, count).Select(i => i == count - 1 ? last : length)];

    private static HlsRenditionOutput[] With(string key, Func<HlsRenditionOutput, HlsRenditionOutput> change)
        => Renditions().Select(r => r.Key == key ? change(r) : r).ToArray();

    private static void Refused(IReadOnlyList<HlsRenditionOutput> renditions, string? mention = null)
    {
        var error = Assert.Throws<ArgumentException>(() => HlsCompletionValidator.Validate(_plan, renditions));
        if (mention is not null)
        {
            Assert.Contains(mention, error.Message, StringComparison.Ordinal);
        }
    }
}
