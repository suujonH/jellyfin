using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.NetworkStorage;
using Jellyfin.Server.Implementations.Tests.Hls;
using MediaBrowser.Common.Net.S3;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Hls;
using MediaBrowser.Model.NetworkStorage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Server.Implementations.Tests.Hls.HlsTestEnvironment;

namespace Jellyfin.Server.Implementations.Tests.NetworkStorage;

public sealed class NetworkStorageInventoryTests : IDisposable
{
    private const string HlsRoot = "s3://media/hls";
    private const string StorageKey = "s3:media";

    private static readonly Guid _seriesId = Guid.Parse("33333333333333333333333333333333");

    private readonly HlsTestEnvironment _environment = new();
    private readonly NetworkStorageManager _manager;
    private readonly NetworkStorageInventory _inventory;
    private readonly Guid _folderId;

    public NetworkStorageInventoryTests()
    {
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(_environment.CreateDbContext);
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(_environment.CreateDbContext);
        var trueCache = new Mock<ITrueCacheService>();
        trueCache.Setup(t => t.Lookup(It.IsAny<string>())).Returns(TrueCacheLookup.None);
        _manager = new NetworkStorageManager(factory.Object, [new S3StorageProtocol(null!)], trueCache.Object, NullLogger<NetworkStorageManager>.Instance);

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(m => m.GetVirtualFolders()).Returns([new VirtualFolderInfo { ItemId = LibraryId.ToString("N"), Name = "剧集", Locations = ["/tv"] }]);
        _inventory = new NetworkStorageInventory(_manager, _environment.Store, _environment.Settings, libraryManager.Object, factory.Object);

        // The folder of the library the items are in.
        _folderId = AddItem(Guid.NewGuid(), "/tv", typeof(Folder).FullName!, null, null, null);
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _manager.Dispose();
        _environment.Dispose();
    }

    [Fact]
    public async Task HlsOfAnItem_IsAResourceOfItsStore_WithEachRenditionAndTheSourceElsewhere()
    {
        await AddAccessAsync();
        var (item, plan) = await _environment.PublishAsync(Sha('a'));
        AddEpisode(item, 1);
        await MoveHlsToStorageAsync();
        var expected = Outputs(plan).ToDictionary(o => o.Key, o => o.Files.Sum(f => f.Length), StringComparer.Ordinal);

        var resource = Assert.Single((await _inventory.GetResourcesAsync(StorageKey, [], null, 0, null, CancellationToken)).Items);

        Assert.Equal(item, resource.ItemId);
        Assert.False(resource.SourceInStorage);
        Assert.Null(resource.SourceStorageKey);
        Assert.Equal(NetworkStorageResourceState.Valid, resource.State);
        Assert.Equal(Sha('a'), resource.Sha256);
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), resource.HlsRenditions.Select(r => r.Key));
        Assert.All(resource.HlsRenditions, r =>
        {
            Assert.Equal(expected[r.Key], r.Size);
            Assert.True(r.InStorage);
            Assert.Equal(StorageKey, r.StorageKey);
            Assert.Equal(HlsRoot, r.Location);
        });
        Assert.Contains(resource.HlsRenditions, r => r.Kind == HlsRenditionKind.Video && r.Quality == 1080);
        Assert.Equal(expected.Values.Sum(), resource.HlsSize);
        Assert.Equal(resource.HlsSize, resource.TotalSize);
        Assert.Equal(LibraryId, resource.LibraryId);
        Assert.Equal("剧集", resource.LibraryName);
    }

    [Fact]
    public async Task HlsInALocalStore_IsInNoStorage()
    {
        var (item, _) = await _environment.PublishAsync(Sha('a'));
        AddEpisode(item, 1);

        Assert.Empty(await _inventory.GetStoragesAsync(CancellationToken));
    }

    [Fact]
    public async Task Summary_CountsEachContentOnce()
    {
        await AddAccessAsync();
        var (item, plan) = await _environment.PublishAsync(Sha('a'));
        var other = await AddItemOfAsync(Sha('a'));
        AddEpisode(item, 1);
        AddEpisode(other, 2);
        await MoveHlsToStorageAsync();

        var media = Assert.Single(await _inventory.GetStoragesAsync(CancellationToken), s => s.Key == StorageKey);

        Assert.Equal(2, media.ResourceCount);
        Assert.Equal(2, media.ValidCount);
        Assert.Equal(Outputs(plan).Sum(o => o.Files.Sum(f => f.Length)), media.HlsSize);
        Assert.Equal(0, media.SourceSize);
        Assert.Equal(media.HlsSize, media.TotalSize);
        Assert.NotNull(media.AccessId);
    }

    [Fact]
    public async Task Groups_AreLibrariesWithTheirSeries_AndOrphansLast()
    {
        await AddAccessAsync();
        var (item, _) = await _environment.PublishAsync(Sha('a'));
        AddEpisode(item, 1);
        var (film, _) = await _environment.PublishAsync(Sha('c'));
        AddItem(film, "/tv/Film.mkv", typeof(Movie).FullName!, null, null, null);
        var (gone, _) = await _environment.PublishAsync(Sha('b'));
        await using (var context = _environment.CreateDbContext())
        {
            await context.HlsSources.Where(s => s.ItemId.Equals(gone)).ExecuteDeleteAsync(CancellationToken);
        }

        await _environment.Store.RefreshOrphansAsync(_environment.Now, CancellationToken);
        await MoveHlsToStorageAsync();

        var groups = await _inventory.GetGroupsAsync(StorageKey, [], CancellationToken);

        Assert.Equal(["library:" + LibraryId.ToString("N"), "orphans"], groups.Select(g => g.Key));
        var library = groups[0];
        Assert.Equal(NetworkStorageGroupKind.Library, library.Kind);
        Assert.Equal("剧集", library.Name);
        Assert.Equal(2, library.ResourceCount);
        Assert.Equal(1, library.LooseCount);
        var series = Assert.Single(library.Children);
        Assert.Equal("series:" + _seriesId.ToString("N"), series.Key);
        Assert.Equal("Show", series.Name);
        Assert.Equal(1, series.ResourceCount);
        Assert.Equal(NetworkStorageGroupKind.Orphans, groups[1].Kind);

        var loose = Assert.Single((await _inventory.GetResourcesAsync(StorageKey, [], library.Key, 0, null, CancellationToken)).Items);
        Assert.Equal(film, loose.ItemId);
        var orphan = Assert.Single((await _inventory.GetResourcesAsync(StorageKey, [], "orphans", 0, null, CancellationToken)).Items);
        Assert.True(orphan.IsOrphan);
        Assert.Equal(Guid.Empty, orphan.ItemId);
        Assert.Equal(Sha('b'), orphan.Sha256);
        Assert.Equal(_environment.Now + TimeSpan.FromHours(_environment.Options.OrphanRetentionHours), orphan.DateOrphanExpires);
        Assert.Empty((await _inventory.GetResourcesAsync(StorageKey, [], "series:nothing", 0, null, CancellationToken)).Items);
    }

    [Fact]
    public async Task Resources_OfASeries_AreInEpisodeOrder()
    {
        await AddAccessAsync();
        var (third, _) = await _environment.PublishAsync(Sha('a'));
        var first = await AddItemOfAsync(Sha('a'));
        var second = await AddItemOfAsync(Sha('a'));
        AddEpisode(third, 3);
        AddEpisode(first, 1);
        AddEpisode(second, 2);
        await MoveHlsToStorageAsync();

        var resources = await _inventory.GetResourcesAsync(StorageKey, [], "series:" + _seriesId.ToString("N"), 0, null, CancellationToken);

        Assert.Equal([first, second, third], resources.Items.Select(r => r.ItemId));
        Assert.Equal([1, 2, 3], resources.Items.Select(r => r.EpisodeNumber ?? 0));
    }

    [Fact]
    public async Task Filter_ByState_LeavesOutEmptyGroups()
    {
        await AddAccessAsync();
        var (item, _) = await _environment.PublishAsync(Sha('a'));
        AddEpisode(item, 1);
        await MoveHlsToStorageAsync();

        Assert.Empty(await _inventory.GetGroupsAsync(StorageKey, [NetworkStorageResourceState.Invalid], CancellationToken));
        Assert.Single(await _inventory.GetGroupsAsync(StorageKey, [NetworkStorageResourceState.Valid], CancellationToken));
    }

    private Task<NetworkStorageAccessDto> AddAccessAsync()
        => _manager.SaveAccessAsync(
            new NetworkStorageAccessDto { Bucket = "media", Endpoint = "http://minio.lan:9000", UsePathStyle = true, AccessKey = "key", SecretKey = "secret" },
            CancellationToken);

    // A new library file whose content is a published one.
    private async Task<Guid> AddItemOfAsync(string sha)
    {
        var item = await _environment.AddSourceAsync();
        await using var context = _environment.CreateDbContext();
        await context.HlsSources.Where(s => s.ItemId.Equals(item)).ExecuteUpdateAsync(s => s.SetProperty(e => e.Sha256, sha), CancellationToken);
        return item;
    }

    // The published HLS, as if its store were the bucket.
    private async Task MoveHlsToStorageAsync()
    {
        await using var context = _environment.CreateDbContext();
        await context.HlsRenditions.ExecuteUpdateAsync(s => s.SetProperty(r => r.Base, HlsRoot), CancellationToken);
    }

    private void AddEpisode(Guid id, int episode)
        => AddItem(id, "/tv/Show/S01E0" + episode + ".mkv", typeof(Episode).FullName!, _seriesId, episode, 1);

    private Guid AddItem(Guid id, string path, string type, Guid? seriesId, int? episode, int? season)
    {
        using var context = _environment.CreateDbContext();
        context.BaseItems.Add(new BaseItemEntity
        {
            Id = id,
            Type = type,
            Path = path,
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            SeriesId = seriesId,
            SeriesName = seriesId is null ? null : "Show",
            IndexNumber = episode,
            ParentIndexNumber = season,
            TopParentId = type == typeof(Folder).FullName ? null : _folderId,
            Size = 1000,
            Data = "{}"
        });
        context.SaveChanges();
        return id;
    }
}
