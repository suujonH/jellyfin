using System;
using Emby.Photos;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Drawing;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Controller.TrueCache;
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

    private readonly Mock<ITrueCacheService> _trueCache = new();
    private readonly DateTime _declared = DateTime.UtcNow;

    public TrueCacheChangeMonitorTests()
    {
        _trueCache.Setup(t => t.HasDeclarationChanged(ResourcePath, It.IsAny<DateTime>())).Returns((string _, DateTime since) => _declared > since);
        BaseItem.MediaSourceManager = Mock.Of<IMediaSourceManager>();
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    public void PhotoProvider_HasChanged_FollowsDeclaration(int refreshedMinutesAfterDeclaration, bool changed)
    {
        var provider = new PhotoProvider(NullLogger<PhotoProvider>.Instance, Mock.Of<IImageProcessor>(), _trueCache.Object);
        var photo = new Photo { Path = ResourcePath, DateLastRefreshed = _declared.AddMinutes(refreshedMinutesAfterDeclaration) };

        Assert.Equal(changed, provider.HasChanged(photo, Mock.Of<IDirectoryService>()));
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(1, false)]
    public void TrickplayProvider_HasChanged_FollowsDeclaration(int refreshedMinutesAfterDeclaration, bool changed)
    {
        var provider = new TrickplayProvider(Mock.Of<IServerConfigurationManager>(), Mock.Of<ITrickplayManager>(), Mock.Of<ILibraryManager>(), _trueCache.Object);
        var video = new Video { Path = ResourcePath, DateLastRefreshed = _declared.AddMinutes(refreshedMinutesAfterDeclaration) };

        Assert.Equal(changed, provider.HasChanged(video, Mock.Of<IDirectoryService>()));
    }
}
