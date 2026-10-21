using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Model.Hls;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Published;

public class HlsEncodingLadderTests
{
    [Theory]
    [InlineData(1920, 800, 1, 1, 0, "1080,720", "1080:1920x800,720:1280x534")] // A wide film keeps its own aspect inside the 16:9 box.
    [InlineData(1920, 800, 1, 1, 0, "1080,720,480", "1080:1920x800,720:1280x534,480:854x356")]
    [InlineData(3840, 2076, 1, 1, 0, "2160", "2160:3840x2076")] // Taller than 16:9 but still fits the 2160 box.
    [InlineData(720, 480, 32, 27, 0, "480", "480:854x480")] // Anamorphic DVD: the SAR widens the display size.
    [InlineData(1918, 798, 1, 1, 0, "1080", "1080:1918x798")] // 2 px under the box: kept as it is, never padded or stretched.
    [InlineData(1917, 1079, 1, 1, 0, "1080", "1080:1918x1080")] // Odd sizes round half away from zero to even.
    [InlineData(1280, 720, 1, 1, 0, "1080", "")] // A 1.5x enlargement is refused.
    [InlineData(1920, 1080, 1, 1, 0, "2160", "")]
    [InlineData(1080, 1920, 1, 1, 0, "1080", "1080:1080x1920")] // Portrait uses the 9:16 box.
    [InlineData(1920, 1080, 1, 1, 90, "1080", "1080:1080x1920")] // A rotation swaps the display size.
    [InlineData(1920, 1080, 1, 1, -90, "1080", "1080:1080x1920")]
    [InlineData(1920, 1080, 1, 1, 270, "1080", "1080:1080x1920")]
    [InlineData(1920, 1080, 1, 1, 180, "1080", "1080:1920x1080")] // Upside down is no swap.
    [InlineData(720, 480, 32, 27, 90, "480", "480:480x854")] // The SAR is applied before the rotation swap.
    public void Select_Size_FollowsTheSixteenByNineBox(int width, int height, int sarNumerator, int sarDenominator, int rotation, string selected, string expected)
        => Assert.Equal(expected, Describe(Select(Probe(width, height, sarNumerator, sarDenominator, rotation), selected)));

    [Fact]
    public void Select_SourceWithinFivePercentOfTheBox_KeepsTheSourceSize()
    {
        // 1920/1830 = 1.0492, 1080/1030 = 1.0485: the smaller factor 1.0485 does not exceed 1.05.
        var quality = Assert.Single(Select(Probe(1830, 1030), "1080"));

        Assert.Equal(new HlsVideoQuality(1080, 1030, 1830, 25), quality);
    }

    [Fact]
    public void Select_SourceBeyondFivePercentOfTheBox_IsNotEnlarged()
        => Assert.Empty(Select(Probe(1824, 1026), "1080")); // 1920/1824 = 1080/1026 = 1.0526 > 1.05.

    [Fact]
    public void Select_Qualities_AreDistinctDescendingAndOfTheLadder()
        => Assert.Equal([1080, 720, 480], Select(Probe(1920, 1080), "480,1080,720,1080,999,0,-5").Select(q => q.Name));

    [Fact]
    public void Select_FractionalFrameRate_IsKept()
    {
        var ladder = HlsEncodingLadder.Select(Probe(1920, 1080, fps: 60000d / 1001), [2160, 1440, 1080, 720, 480]);

        Assert.Equal([1080, 720, 480], ladder.Select(q => q.Name));
        Assert.Equal(60000d / 1001, ladder[0].FramesPerSecond);
        Assert.Equal(30000d / 1001, ladder[2].FramesPerSecond);
        Assert.All(ladder, q => Assert.True(q.Width % 2 == 0 && q.Height % 2 == 0));
    }

    [Fact]
    public void Select_FrameRate_IsCappedAtSixtyAndHalvedOnlyBelow720()
    {
        Assert.Equal(60, Assert.Single(Select(Probe(1920, 1080, fps: 120), "1080")).FramesPerSecond);
        Assert.Equal([50d, 50d, 25d, 25d], Select(Probe(1920, 1080, fps: 50), "1080,720,480,360").Select(q => q.FramesPerSecond));
    }

    [Fact]
    public void Select_RotatedPortrait_KeepsTheNameApartFromTheHeight()
        => Assert.Equal(new HlsVideoQuality(720, 1280, 720, 25), Assert.Single(HlsEncodingLadder.Select(Probe(1920, 1080, rotation: 90), [720])));

    private static HlsProbe Probe(int width, int height, int sarNumerator = 1, int sarDenominator = 1, int rotation = 0, double fps = 25)
        => new(width, height, fps, 60, "h264", "yuv420p", "bt709", "progressive", rotation, [], [], 0, sarNumerator, sarDenominator);

    private static IReadOnlyList<HlsVideoQuality> Select(HlsProbe probe, string heights)
        => HlsEncodingLadder.Select(probe, heights.Split(',').Select(h => int.Parse(h, CultureInfo.InvariantCulture)));

    private static string Describe(IEnumerable<HlsVideoQuality> ladder)
        => string.Join(',', ladder.Select(q => string.Create(CultureInfo.InvariantCulture, $"{q.Name}:{q.Width}x{q.Height}")));
}
