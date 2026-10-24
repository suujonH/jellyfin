using System.Collections.Generic;
using Jellyfin.Server.Implementations.Hls;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Hls;

public class HlsQualityRulesTests
{
    [Theory]
    [InlineData("v1080", true)]
    [InlineData("v720", true)]
    [InlineData("a0", false)]
    [InlineData("s1", false)]
    [InlineData("preview", false)]
    [InlineData("v", false)]
    [InlineData("vx", false)]
    public void IsVideoKey_TellsTheVideoQualities(string key, bool expected)
        => Assert.Equal(expected, HlsQualityRules.IsVideoKey(key));

    [Theory]
    [InlineData(new[] { "v720", "a0" }, new int[0], true)]
    [InlineData(new[] { "a0", "preview" }, new[] { 720 }, true)]
    [InlineData(new[] { "v720", "a0" }, new[] { 720 }, false)]
    [InlineData(new[] { "v1080", "v720" }, new[] { 720 }, true)]
    [InlineData(new string[0], new[] { 720 }, true)]
    public void CanTake_NeedsAVideoQualityNotExcluded(string[] keys, int[] excluded, bool expected)
        => Assert.Equal(expected, HlsQualityRules.CanTake(keys, new HashSet<int>(excluded)));

    [Fact]
    public void IsExcluded_OnlyCountsTheVideoQualitiesExcluded()
    {
        var excluded = new HashSet<int> { 720 };
        Assert.True(HlsQualityRules.IsExcluded("v720", excluded));
        Assert.False(HlsQualityRules.IsExcluded("v1080", excluded));
        Assert.False(HlsQualityRules.IsExcluded("a720", excluded));
    }
}
