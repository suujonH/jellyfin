using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Published;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Hls;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Server.Implementations.Tests.Hls.HlsTestEnvironment;

namespace Jellyfin.Server.Implementations.Tests.Hls;

public sealed class HlsVersionCatalogTests : IDisposable
{
    private readonly ILibraryManager _previousLibraryManager = BaseItem.LibraryManager;
    private readonly HlsTestEnvironment _environment = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<IMediaStreamRepository> _mediaStreams = new();
    private readonly Dictionary<Guid, BaseItem> _items = [];
    private readonly Dictionary<Guid, IReadOnlyList<MediaStream>> _savedStreams = [];
    private readonly List<Guid> _updated = [];
    private readonly Folder _folder = new() { Id = Guid.NewGuid(), Name = "Films" };
    private readonly HlsVersionCatalog _catalog;
    private DateTime _now = new(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);

    public HlsVersionCatalogTests()
    {
        // The ids of the version items stand for those of the library, which hashes the path the same way every time.
        _libraryManager.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns<string, Type>((key, _) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key))));
        _libraryManager.Setup(l => l.GetItemById(It.IsAny<Guid>())).Returns((Guid id) => _items.GetValueOrDefault(id));
        _libraryManager.Setup(l => l.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem?>())).Callback<BaseItem, BaseItem?>((item, _) => _items[item.Id] = item);
        _libraryManager.Setup(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .Callback<BaseItem, BaseItem, ItemUpdateType, CancellationToken>((item, _, _, _) => _updated.Add(item.Id))
            .Returns(Task.CompletedTask);
        _libraryManager.Setup(l => l.DeleteItem(It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>()))
            .Callback<BaseItem, DeleteOptions>((item, options) =>
            {
                Assert.False(options.DeleteFileLocation);

                // Not deleted as an alternate version, which would clean up what the original extracted.
                Assert.Null(((Video)item).PrimaryVersionId);
                _items.Remove(item.Id);
            });
        BaseItem.LibraryManager = _libraryManager.Object;
        _items[_folder.Id] = _folder;

        _mediaStreams.Setup(m => m.GetMediaStreams(It.IsAny<MediaStreamQuery>()))
            .Returns([new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, IsForced = true }]);
        _mediaStreams.Setup(m => m.SaveMediaStreams(It.IsAny<Guid>(), It.IsAny<IReadOnlyList<MediaStream>>(), It.IsAny<CancellationToken>()))
            .Callback<Guid, IReadOnlyList<MediaStream>, CancellationToken>((id, streams, _) => _savedStreams[id] = streams);

        _catalog = new HlsVersionCatalog(_environment.Store, _environment.Index, _environment.Settings, _libraryManager.Object, _mediaStreams.Object, NullLogger<HlsVersionCatalog>.Instance);
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _catalog.Dispose();
        _environment.Dispose();
        BaseItem.LibraryManager = _previousLibraryManager;
    }

    [Theory]
    [InlineData("0123456789abcdef0123456789abcdef:720", 720)]
    [InlineData("0123456789abcdef0123456789abcdef:0:legacy", 0)]
    public void Identify_ReadsTheMarker(string marker, int quality)
    {
        var item = new Video { ProviderIds = new() { [HlsVersionCatalog.Marker] = marker } };

        Assert.Equal((LibraryId, quality), HlsVersionCatalog.Identify(item));
        Assert.True(HlsVersionCatalog.IsVersionItem(item));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0123456789abcdef0123456789abcdef")]
    [InlineData("film:720")]
    [InlineData("0123456789abcdef0123456789abcdef:-1")]
    public void Identify_NoValidMarker_IsNull(string? marker)
    {
        var item = new Video();
        if (marker is not null)
        {
            item.ProviderIds[HlsVersionCatalog.Marker] = marker;
        }

        Assert.Null(HlsVersionCatalog.Identify(item));
        Assert.Null(HlsVersionCatalog.Identify(null));
    }

    [Fact]
    public async Task Tick_BeforeTheIndexIsLoaded_DoesNothing()
    {
        var original = await PublishAsync(load: false);

        await TickAsync();

        Assert.Equal([_folder.Id, original.Id], _items.Keys);
        Assert.Empty(await _environment.Store.GetVersionItemsAsync(CancellationToken));
    }

    [Fact]
    public async Task Tick_CreatesAVersionItemPerQualityAndAuto()
    {
        var original = await PublishAsync();
        original.Tags = ["Tag"];
        original.ProviderIds["Imdb"] = "tt0000001";
        original.ImageInfos = [new ItemImageInfo { Path = "/media/poster.jpg", Type = ImageType.Primary }];

        await TickAsync();

        var rows = (await _environment.Store.GetVersionItemsAsync(CancellationToken)).OrderBy(r => r.Quality).ToList();
        Assert.Equal([0, 720, 1080], rows.Select(r => r.Quality));
        foreach (var row in rows)
        {
            Assert.Equal(HlsVersionCatalog.GetVersionId(_libraryManager.Object, original.Id, row.Quality), row.VersionId);
            var version = Assert.IsType<Video>(_items[row.VersionId]);
            Assert.Equal((original.Id, row.Quality), HlsVersionCatalog.Identify(version));
            Assert.Equal([HlsVersionCatalog.Marker], version.ProviderIds.Keys);
            Assert.Equal("https://distributed-hls.invalid/" + original.Id.ToString("N") + "/" + row.Quality + "/master.m3u8", version.Path);
            Assert.Equal(original.Id, version.PrimaryVersionId);
            Assert.Equal(_folder.Id, version.ParentId);
            Assert.Equal(Guid.Empty, version.OwnerId);
            Assert.Equal("Film", version.Name);
            Assert.Equal("Film, The", version.ForcedSortName);
            Assert.Equal("hls", version.Container);
            Assert.True(version.IsLocked);
            Assert.Equal(["Tag"], version.Tags);
            Assert.Equal("/media/poster.jpg", Assert.Single(version.ImageInfos).Path);
            Assert.NotSame(original.ImageInfos[0], version.ImageInfos[0]);
            Assert.Equal(TimeSpan.FromSeconds(Probe.DurationSeconds).Ticks, version.RunTimeTicks);

            // The streams keep the dispositions of the original.
            Assert.True(_savedStreams[row.VersionId].Single(s => s.Type == MediaStreamType.Subtitle).IsForced);
        }

        // "auto" has the size of the highest quality.
        Assert.Equal(((Video)_items[rows[0].VersionId]).Height, ((Video)_items[rows[2].VersionId]).Height);
        Assert.Empty(_updated);
    }

    [Fact]
    public async Task Tick_UnchangedOriginal_IsNotWrittenAgain_AChangedOneIs()
    {
        var original = await PublishAsync();
        await TickAsync();

        _libraryManager.Raise(l => l.ItemUpdated += null, _libraryManager.Object, new ItemChangeEventArgs { Item = original });
        await TickAsync();
        Assert.Empty(_updated);

        original.Overview = "Changed.";
        _libraryManager.Raise(l => l.ItemUpdated += null, _libraryManager.Object, new ItemChangeEventArgs { Item = original });
        await TickAsync();
        Assert.Equal(3, _updated.Count);
        Assert.All(_items.Values.OfType<Video>().Where(HlsVersionCatalog.IsVersionItem), v => Assert.Equal("Changed.", v.Overview));
    }

    [Fact]
    public async Task Tick_TurnedOff_DeletesTheVersionItems()
    {
        var original = await PublishAsync();
        await TickAsync();

        _environment.Save(new HlsOptions { Qualities = Qualities });
        await TickAsync();

        Assert.Equal([_folder.Id, original.Id], _items.Keys);
        Assert.Empty(await _environment.Store.GetVersionItemsAsync(CancellationToken));
    }

    [Fact]
    public async Task Tick_RemovedOriginal_DeletesItsVersionItems()
    {
        var original = await PublishAsync();
        await TickAsync();

        _items.Remove(original.Id);
        _libraryManager.Raise(l => l.ItemRemoved += null, _libraryManager.Object, new ItemChangeEventArgs { Item = original });
        await TickAsync();

        Assert.Equal([_folder.Id], _items.Keys);
        Assert.Empty(await _environment.Store.GetVersionItemsAsync(CancellationToken));
    }

    [Fact]
    public async Task Tick_UnpublishedQuality_DeletesItsVersionItem()
    {
        var original = await PublishAsync();
        await TickAsync();
        var plan = await _environment.Store.GetVersionItemsAsync(CancellationToken);

        var directory = _environment.Index.GetMediaForItem(original.Id)!.Find(HlsRenditionKind.Video, 720)!.RelativeDirectory;
        Assert.True(await _environment.Store.RetireRenditionAsync(Sha('a'), "v720", directory, "Gone.", _environment.Now, CancellationToken));
        await _environment.Index.RefreshMediaAsync([Sha('a')], CancellationToken);
        await TickAsync();

        Assert.Equal([0, 1080], (await _environment.Store.GetVersionItemsAsync(CancellationToken)).Select(r => r.Quality).Order());
        Assert.False(_items.ContainsKey(plan.Single(r => r.Quality == 720).VersionId));
    }

    [Fact]
    public async Task Tick_FailedHalfway_IsRetriedOrDeletedLater()
    {
        var original = await PublishAsync();
        var failing = HlsVersionCatalog.GetVersionId(_libraryManager.Object, original.Id, 720);
        _mediaStreams.Setup(m => m.SaveMediaStreams(failing, It.IsAny<IReadOnlyList<MediaStream>>(), It.IsAny<CancellationToken>()))
            .Throws(new InvalidOperationException("The database is busy."));
        await TickAsync();

        // The item created before the failure is recorded, without what it was written from.
        var rows = (await _environment.Store.GetVersionItemsAsync(CancellationToken)).ToDictionary(r => r.Quality);
        Assert.Equal([0, 720, 1080], rows.Keys.Order());
        Assert.NotEmpty(rows[1080].Signature);
        Assert.Empty(rows[720].Signature);
        Assert.True(_items.ContainsKey(failing));

        _environment.Save(new HlsOptions { Qualities = Qualities });
        await TickAsync();

        Assert.Equal([_folder.Id, original.Id], _items.Keys);
        Assert.Empty(await _environment.Store.GetVersionItemsAsync(CancellationToken));
    }

    [Fact]
    public async Task Tick_FailedUpdate_IsWrittenOnTheRetry()
    {
        var original = await PublishAsync();
        await TickAsync();
        _libraryManager.SetupSequence(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The database is busy."))
            .Returns(Task.CompletedTask)
            .Returns(Task.CompletedTask)
            .Returns(Task.CompletedTask);

        original.Overview = "Changed.";
        _libraryManager.Raise(l => l.ItemUpdated += null, _libraryManager.Object, new ItemChangeEventArgs { Item = original });
        await TickAsync();

        // The retry writes every version item: the one that failed, whose cached item already holds the change, too.
        await TickAsync(TimeSpan.FromMinutes(10));
        _libraryManager.Verify(l => l.UpdateItemAsync(It.IsAny<BaseItem>(), It.IsAny<BaseItem>(), It.IsAny<ItemUpdateType>(), It.IsAny<CancellationToken>()), Times.Exactly(4));
        Assert.All(await _environment.Store.GetVersionItemsAsync(CancellationToken), r => Assert.NotEmpty(r.Signature));
    }

    // A published library file and the item of it.
    private async Task<Video> PublishAsync(bool load = true)
    {
        var (itemId, _) = await _environment.PublishAsync(Sha('a'));
        var original = new Video { Id = itemId, Name = "Film", SortName = "Film, The", ParentId = _folder.Id, Path = "/media/film.mkv" };
        _items[itemId] = original;
        if (load)
        {
            await _environment.Index.LoadAsync(CancellationToken);
        }

        return original;
    }

    private Task TickAsync(TimeSpan? after = null)
    {
        _now += after ?? TimeSpan.FromSeconds(5);
        return _catalog.TickAsync(_now, CancellationToken);
    }
}
