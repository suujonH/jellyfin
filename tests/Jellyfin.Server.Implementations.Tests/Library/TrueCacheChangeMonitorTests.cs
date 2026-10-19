using System;
using System.Collections.Generic;
using System.IO;
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
/// Change monitors that read a declaration instead of the resource rerun their provider when only <c>meta.toml</c> changes.
/// The resource itself is unchanged in every test: the directory service does not know it.
/// </summary>
public sealed class TrueCacheChangeMonitorTests : IDisposable
{
    private const string ResourcePath = "/media/Film.mkv";

    private readonly string _entryDirectory = Path.Combine(Path.GetTempPath(), "jellyfin-truecache-" + Guid.NewGuid().ToString("N"));
    private readonly Mock<ITrueCacheService> _trueCache = new();
    private readonly DateTime _declared;

    public TrueCacheChangeMonitorTests()
    {
        Directory.CreateDirectory(_entryDirectory);
        var meta = Path.Combine(_entryDirectory, TrueCacheLayout.MetaFile);
        File.WriteAllText(meta, "version = 1");
        _declared = File.GetLastWriteTimeUtc(meta);

        var entry = new TrueCacheEntry(ResourcePath, _entryDirectory, new TrueCacheManifest(), null, null, null, new Dictionary<ImageType, string>(), new Dictionary<int, string>());
        _trueCache.Setup(t => t.Lookup(ResourcePath)).Returns(TrueCacheLookup.Valid(entry));
        BaseItem.MediaSourceManager = Mock.Of<IMediaSourceManager>();
    }

    public void Dispose() => Directory.Delete(_entryDirectory, true);

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
