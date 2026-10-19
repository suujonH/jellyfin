using System;
using System.IO;
using System.Threading.Tasks;
using Emby.Server.Implementations.IO;
using Emby.Server.Implementations.Library;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.IO;

public class LibraryMonitorTrueCacheTests
{
    [Fact]
    public async Task ReportFileSystemChanged_CacheFolderItself_RefreshesTheFolderHoldingIt()
    {
        var tempDir = Directory.CreateTempSubdirectory("librarymonitor");
        try
        {
            // A cache folder moved in or out as a whole raises one event for the folder and none for what it holds,
            // yet it changes the entry of every resource next to it.
            // A proxy rather than a subclass: BaseItemKindTests requires every BaseItem type of this assembly to be a real kind.
            var folder = new Mock<Folder> { CallBase = true };
            var folderRefreshed = new TaskCompletionSource();
            folder.Setup(f => f.ChangedExternally()).Callback(() => folderRefreshed.TrySetResult());
            folder.Object.Id = Guid.NewGuid();
            folder.Object.Path = tempDir.FullName;
            var libraryManager = new Mock<ILibraryManager>(MockBehavior.Loose);
            libraryManager.Setup(x => x.FindByPath(tempDir.FullName, null)).Returns(folder.Object);

            var fileSystem = new Mock<IFileSystem>(MockBehavior.Loose);
            fileSystem.Setup(x => x.GetFileSystemInfo(It.IsAny<string>()))
                .Returns((string path) => new FileSystemMetadata { FullName = path, Name = Path.GetFileName(path), IsDirectory = true, Exists = true });

            var configurationManager = new Mock<IServerConfigurationManager>(MockBehavior.Loose);
            configurationManager.Setup(x => x.Configuration)
                .Returns(new ServerConfiguration { LibraryMonitorDelay = 1 });

            var trueCache = new Mock<ITrueCacheService>();
            trueCache.Setup(t => t.Lookup(It.IsAny<string>())).Returns(TrueCacheLookup.None);

            using var monitor = new LibraryMonitor(
                NullLogger<LibraryMonitor>.Instance,
                libraryManager.Object,
                configurationManager.Object,
                fileSystem.Object,
                Mock.Of<IDirectoryService>(),
                Mock.Of<IHostApplicationLifetime>(),
                new DotIgnoreIgnoreRule(),
                trueCache.Object);

            monitor.ReportFileSystemChanged(Path.Combine(tempDir.FullName, TrueCacheLayout.FolderName));

            await folderRefreshed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        finally
        {
            tempDir.Delete(true);
        }
    }
}
