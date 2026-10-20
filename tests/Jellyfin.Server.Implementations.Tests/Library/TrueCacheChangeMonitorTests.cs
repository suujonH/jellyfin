using System.Collections.Generic;
using Emby.Photos;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Entities;
using MediaBrowser.Providers.Trickplay;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Library;

/// <summary>
/// Change monitors that read a declaration instead of the resource rerun their provider when only the declaration changes.
/// The resource itself is unchanged in every test: the directory service does not know it.
/// </summary>
public sealed class TrueCacheChangeMonitorTests
{
    private const string ResourcePath = "/media/Film.mkv";
    private const string DeclaredHash = "declared";

    private readonly Mock<ITrueCacheService> _trueCache = new();

    public TrueCacheChangeMonitorTests()
    {
        var entry = new TrueCacheEntry(ResourcePath, "/media/__true_cache__/Film.mkv", DeclaredHash, new TrueCacheManifest(), null, null, null, new Dictionary<ImageType, string>(), new Dictionary<int, string>());
        _trueCache.Setup(t => t.Lookup(ResourcePath)).Returns(TrueCacheLookup.Valid(entry));
        BaseItem.MediaSourceManager = Mock.Of<IMediaSourceManager>();
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("older", true)]
    [InlineData(DeclaredHash, false)]
    public void PhotoProvider_HasChanged_FollowsDeclaration(string? refreshedHash, bool changed)
    {
        var provider = new PhotoProvider(NullLogger<PhotoProvider>.Instance, Mock.Of<IImageProcessor>(), _trueCache.Object);
        var photo = new Photo { Path = ResourcePath, TrueCacheHash = refreshedHash };

        Assert.Equal(changed, provider.HasChanged(photo, Mock.Of<IDirectoryService>()));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("older", true)]
    [InlineData(DeclaredHash, false)]
    public void TrickplayProvider_HasChanged_FollowsDeclaration(string? refreshedHash, bool changed)
    {
        var provider = new TrickplayProvider(Mock.Of<IServerConfigurationManager>(), Mock.Of<ITrickplayManager>(), Mock.Of<ILibraryManager>(), _trueCache.Object);
        var video = new Video { Path = ResourcePath, TrueCacheHash = refreshedHash };

        Assert.Equal(changed, provider.HasChanged(video, Mock.Of<IDirectoryService>()));
    }
}
