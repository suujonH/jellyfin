using Emby.Server.Implementations.Library;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.IO;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

public class TrueCacheIgnoreRuleTests
{
    [Fact]
    public void ShouldIgnore_CacheFolder_IsIgnored()
    {
        var rule = new TrueCacheIgnoreRule(NoTrueCacheService.Instance);

        Assert.True(rule.ShouldIgnore(new FileSystemMetadata { FullName = "/media/__true_cache__", Name = "__true_cache__", IsDirectory = true }, null));
    }

    [Fact]
    public void ShouldIgnore_FileNamedLikeCacheFolder_IsNotIgnored()
    {
        var rule = new TrueCacheIgnoreRule(NoTrueCacheService.Instance);

        Assert.False(rule.ShouldIgnore(new FileSystemMetadata { FullName = "/media/__true_cache__", Name = "__true_cache__", IsDirectory = false }, null));
    }

    [Theory]
    [InlineData(TrueCacheState.Rejected, true)]
    [InlineData(TrueCacheState.None, false)]
    public void ShouldIgnore_Resource_FollowsEntryState(TrueCacheState state, bool ignored)
    {
        var trueCache = new Mock<ITrueCacheService>();
        trueCache.Setup(t => t.Lookup("/media/Film.mkv"))
            .Returns(state == TrueCacheState.Rejected ? TrueCacheLookup.Rejected("missing probe.json") : TrueCacheLookup.None);
        var rule = new TrueCacheIgnoreRule(trueCache.Object);

        Assert.Equal(ignored, rule.ShouldIgnore(new FileSystemMetadata { FullName = "/media/Film.mkv", Name = "Film.mkv" }, null));
    }
}
