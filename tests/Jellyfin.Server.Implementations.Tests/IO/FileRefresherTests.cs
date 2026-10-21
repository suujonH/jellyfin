using System;
using System.IO;
using System.Threading.Tasks;
using Emby.Server.Implementations.IO;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.IO;

public class FileRefresherTests
{
    [Fact]
    public async Task ProcessPathChanges_PathLookupThrows_StillRefreshesRemainingPaths()
    {
        var tempDir = Directory.CreateTempSubdirectory("filerefresher");
        try
        {
            // Ordered so the failing path is dequeued first.
            var failingPath = Path.Combine(tempDir.FullName, "failing", "episode.mkv");
            var workingPath = Directory.CreateDirectory(Path.Combine(tempDir.FullName, "working")).FullName;

            var workingItem = new Folder { Path = workingPath, Name = "working" };
            var workingItemFound = new TaskCompletionSource();

            var libraryManager = new Mock<ILibraryManager>(MockBehavior.Loose);
            libraryManager.Setup(x => x.FindByPath(failingPath, null))
                .Throws(new ObjectDisposedException("IServiceProvider"));
            libraryManager.Setup(x => x.FindByPath(workingPath, null))
                .Returns(workingItem)
                .Callback(() => workingItemFound.TrySetResult());

            var configurationManager = new Mock<IServerConfigurationManager>(MockBehavior.Loose);
            configurationManager.Setup(x => x.Configuration)
                .Returns(new ServerConfiguration { LibraryMonitorDelay = 1 });

            using var refresher = new FileRefresher(
                failingPath,
                configurationManager.Object,
                libraryManager.Object,
                NoTrueCacheService.Instance,
                NullLogger.Instance);
            refresher.AddPath(workingPath);

            await workingItemFound.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            libraryManager.Verify(x => x.FindByPath(failingPath, null), Times.Once);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ProcessPathChanges_ResourceWithRejectedEntry_IsHandledLikeAMissingFile(bool rejected)
    {
        var tempDir = Directory.CreateTempSubdirectory("filerefresher");
        try
        {
            // Refreshing the item only reruns its providers; like a deleted file, a rejected resource needs the
            // refresh of a parent, whose validation of its children removes it from the library.
            var resourcePath = Path.Combine(tempDir.FullName, "Film.mkv");
            await File.WriteAllBytesAsync(resourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
            var resource = CreateVideo(resourcePath);

            // Items are refreshed in the order of their paths, so once the sentinel is refreshed the resource has had its turn.
            var sentinelPath = Path.Combine(tempDir.FullName, "Sentinel.mkv");
            await File.WriteAllBytesAsync(sentinelPath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
            var sentinel = CreateVideo(sentinelPath);
            var sentinelRefreshed = new TaskCompletionSource();
            sentinel.Setup(v => v.ChangedExternally()).Callback(() => sentinelRefreshed.TrySetResult());

            var libraryManager = new Mock<ILibraryManager>(MockBehavior.Loose);
            libraryManager.Setup(x => x.FindByPath(resourcePath, null)).Returns(resource.Object);
            libraryManager.Setup(x => x.FindByPath(sentinelPath, null)).Returns(sentinel.Object);

            var trueCache = new Mock<ITrueCacheService>();
            trueCache.Setup(t => t.Lookup(It.IsAny<string>())).Returns(TrueCacheLookup.None);
            if (rejected)
            {
                trueCache.Setup(t => t.Lookup(resourcePath)).Returns(TrueCacheLookup.Rejected("missing probe.json"));
            }

            var configurationManager = new Mock<IServerConfigurationManager>(MockBehavior.Loose);
            configurationManager.Setup(x => x.Configuration)
                .Returns(new ServerConfiguration { LibraryMonitorDelay = 1 });

            using var refresher = new FileRefresher(
                resourcePath,
                configurationManager.Object,
                libraryManager.Object,
                trueCache.Object,
                NullLogger.Instance);
            refresher.AddPath(sentinelPath);

            await sentinelRefreshed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            resource.Verify(v => v.ChangedExternally(), rejected ? Times.Never() : Times.Once());
        }
        finally
        {
            tempDir.Delete(true);
        }
    }

    // A proxy rather than a subclass: BaseItemKindTests requires every BaseItem type of this assembly to be a real kind.
    private static Mock<Video> CreateVideo(string path)
    {
        var video = new Mock<Video> { CallBase = true };
        video.Setup(v => v.ChangedExternally());
        video.Object.Id = Guid.NewGuid();
        video.Object.Path = path;
        return video;
    }
}
