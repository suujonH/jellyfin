using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Implementations.NetworkStorage;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.NetworkStorage;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.NetworkStorage;

public sealed class NetworkStorageManagerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly Mock<ITrueCacheService> _trueCache = new();
    private readonly CheckingS3Protocol _s3 = new();
    private readonly NetworkStorageManager _manager;

    public NetworkStorageManagerTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options;
        using (var context = CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        _trueCache.Setup(t => t.Lookup(It.IsAny<string>())).Returns(TrueCacheLookup.None);
        _manager = CreateManager();
    }

    public void Dispose() => _connection.Dispose();

    // A new manager loads what the database holds, as after a restart.
    private NetworkStorageManager CreateManager()
    {
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContext()).Returns(CreateDbContext);
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateDbContext);
        return new NetworkStorageManager(
            factory.Object,
            [_s3, new HttpStorageProtocol(null!)],
            _trueCache.Object,
            NullLogger<NetworkStorageManager>.Instance);
    }

    [Fact]
    public async Task ResolveShortcutTarget_S3_IsPresignedWithTheAccessOfItsBucket()
    {
        await AddAccessAsync("media");

        var url = _manager.ResolveShortcutTarget("s3://media/Movies/Film.mkv");

        Assert.StartsWith("http://minio.lan:9000/media/Movies/Film.mkv?X-Amz-Algorithm=AWS4-HMAC-SHA256&", url, StringComparison.Ordinal);
        Assert.Null(_manager.ResolveShortcutTarget("s3://other/Film.mkv"));
        Assert.Null(_manager.ResolveShortcutTarget("s3://"));
        Assert.Equal("https://cdn.example.com/Film.mkv", _manager.ResolveShortcutTarget("https://cdn.example.com/Film.mkv"));
        Assert.Equal("rtsp://camera/stream", _manager.ResolveShortcutTarget("rtsp://camera/stream"));
    }

    [Fact]
    public async Task ResolveShortcutTarget_PublishedStorage_IsReadAtItsExternalAddress()
    {
        var access = await AddAccessAsync("media");
        await _manager.SaveExternalAddressAsync(
            new NetworkStorageExternalAddressDto { AccessId = access.Id, AuthMode = ExternalAuthMode.CloudflareWorker, BaseUrl = "https://media.example.com", Secret = "shared" },
            CancellationToken.None);

        var url = _manager.ResolveShortcutTarget("s3://media/Movies/Film.mkv");

        Assert.StartsWith("https://media.example.com/media/Movies/Film.mkv?exp=", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveShortcutTarget_StorageNotPublic_IsReadAtTheEndpoint()
    {
        var access = await AddAccessAsync("media");
        var saved = await _manager.SaveExternalAddressAsync(new NetworkStorageExternalAddressDto { AccessId = access.Id }, CancellationToken.None);

        // No external address is what keeps a storage private.
        Assert.False(saved.IsPublic);
        Assert.StartsWith("http://minio.lan:9000/media/Film.mkv?X-Amz-Algorithm=", _manager.ResolveShortcutTarget("s3://media/Film.mkv"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetExternalAddress_MadePrivateWithAMethodLeft_StaysPrivate()
    {
        // Written when being public was a switch of its own: the method was kept while the switch was off.
        var access = await AddAccessAsync("media");
        await _manager.SaveExternalAddressAsync(
            new NetworkStorageExternalAddressDto { AccessId = access.Id, AuthMode = ExternalAuthMode.PublicBucket, BaseUrl = "https://pub.example.com" },
            CancellationToken.None);
        await using (var context = CreateDbContext())
        {
            (await context.NetworkStorageExternalAddresses.SingleAsync(TestContext.Current.CancellationToken)).IsPublic = false;
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using var manager = CreateManager();
        var external = manager.GetExternalAddress(access.Id);

        Assert.False(external.IsPublic);
        Assert.Null(external.AuthMode);
        Assert.StartsWith("http://minio.lan:9000/", manager.ResolveShortcutTarget("s3://media/Film.mkv"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SaveAccessAsync_NullSecret_KeepsTheStoredOne()
    {
        var access = await AddAccessAsync("media");

        var saved = await _manager.SaveAccessAsync(new NetworkStorageAccessDto { Id = access.Id, Bucket = "media", AccessKey = "key2" }, CancellationToken.None);

        Assert.Equal("key2", saved.AccessKey);
        Assert.Equal("secret", saved.SecretKey);
        Assert.Null((await _manager.SaveAccessAsync(new NetworkStorageAccessDto { Id = access.Id, Bucket = "media", SecretKey = string.Empty }, CancellationToken.None)).SecretKey);
    }

    [Fact]
    public async Task SaveAccessAsync_SecondAccessOfABucket_IsRefused()
    {
        await AddAccessAsync("media");

        await Assert.ThrowsAsync<ArgumentException>(() => _manager.SaveAccessAsync(new NetworkStorageAccessDto { Bucket = "media" }, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => _manager.SaveAccessAsync(new NetworkStorageAccessDto { Bucket = "bad/name" }, CancellationToken.None));
    }

    [Fact]
    public async Task PrepareForClient_StorageNotPublic_LeavesNoAddressOfIt()
    {
        await AddAccessAsync("media");
        var source = new MediaSourceInfo { Path = _manager.ResolveShortcutTarget("s3://media/Film.mkv"), ShortcutTarget = "s3://media/Film.mkv", SupportsDirectPlay = true };

        _manager.PrepareForClient(source);

        // The presigned address carries the server's credentials. Like a local file, the client may still play the
        // source directly, from the static stream the server proxies.
        Assert.Equal("s3://media/Film.mkv", source.Path);
        Assert.Equal(MediaProtocol.File, source.Protocol);
        Assert.True(source.SupportsDirectPlay);
    }

    [Fact]
    public async Task PrepareForClient_PublishedThroughAWorker_GetsAWorkerAddress()
    {
        var access = await AddAccessAsync("media");
        await _manager.SaveExternalAddressAsync(
            new NetworkStorageExternalAddressDto { AccessId = access.Id, AuthMode = ExternalAuthMode.CloudflareWorker, BaseUrl = "https://media.example.com/", Secret = "shared" },
            CancellationToken.None);
        var source = new MediaSourceInfo { Path = "http://minio.lan:9000/x", ShortcutTarget = "s3://media/A B.mkv", SupportsDirectPlay = true };

        _manager.PrepareForClient(source);

        Assert.StartsWith("https://media.example.com/media/A%20B.mkv?exp=", source.Path, StringComparison.Ordinal);
        Assert.Contains("&sig=", source.Path, StringComparison.Ordinal);
        Assert.True(source.SupportsDirectPlay);
    }

    [Fact]
    public void NewAccess_IsAnArchiveByDefault()
    {
        Assert.True(new NetworkStorageAccessDto().IsArchive);
        Assert.True(new NetworkStorageAccess { Protocol = "s3", Bucket = "media" }.IsArchive);
    }

    [Fact]
    public async Task PrepareForClient_Archive_CannotBePlayedInAnyWay()
    {
        var access = await AddAccessAsync("media", isArchive: true);
        await _manager.SaveExternalAddressAsync(
            new NetworkStorageExternalAddressDto { AccessId = access.Id, AuthMode = ExternalAuthMode.PublicBucket, BaseUrl = "https://pub.example.com" },
            CancellationToken.None);
        var source = new MediaSourceInfo
        {
            Path = _manager.ResolveShortcutTarget("s3://media/Film.mkv"),
            ShortcutTarget = "s3://media/Film.mkv",
            SupportsDirectPlay = true,
            SupportsDirectStream = true,
            SupportsTranscoding = true
        };

        _manager.PrepareForClient(source);

        // Even published, an archive is not handed out, streamed or transcoded.
        Assert.Equal("s3://media/Film.mkv", source.Path);
        Assert.False(source.SupportsDirectPlay);
        Assert.False(source.SupportsDirectStream);
        Assert.False(source.SupportsTranscoding);
    }

    [Fact]
    public async Task IsArchived_FollowsTheAccessOfTheBucket()
    {
        var archive = await AddAccessAsync("archive", isArchive: true);
        await AddAccessAsync("media");

        Assert.True(_manager.IsArchived("s3://archive/Film.mkv"));
        Assert.False(_manager.IsArchived("s3://media/Film.mkv"));
        Assert.False(_manager.IsArchived("s3://other/Film.mkv"));
        Assert.False(_manager.IsArchived("https://cdn.example.com/Film.mkv"));
        Assert.False(_manager.IsArchived(null));

        // Probing still reads an archive.
        Assert.NotNull(_manager.ResolveShortcutTarget("s3://archive/Film.mkv"));

        archive.IsArchive = false;
        archive.SecretKey = null;
        await _manager.SaveAccessAsync(archive, CancellationToken.None);
        Assert.False(_manager.IsArchived("s3://archive/Film.mkv"));
    }

    [Fact]
    public void PrepareForClient_HttpTarget_IsLeftAsWritten()
    {
        var source = new MediaSourceInfo { Path = "https://cdn.example.com/Film.mkv", ShortcutTarget = "https://cdn.example.com/Film.mkv", SupportsDirectPlay = true };

        _manager.PrepareForClient(source);

        Assert.Equal("https://cdn.example.com/Film.mkv", source.Path);
        Assert.True(source.SupportsDirectPlay);
    }

    [Theory]
    [InlineData(ExternalAuthMode.CloudflareWorker, "https://media.example.com", null)]
    [InlineData(ExternalAuthMode.CloudflareWorker, null, "shared")]
    [InlineData(ExternalAuthMode.PublicBucket, null, null)]
    public async Task SaveExternalAddressAsync_WithoutWhatItsAuthenticationNeeds_IsRefused(ExternalAuthMode mode, string? baseUrl, string? secret)
    {
        var access = await AddAccessAsync("media");

        await Assert.ThrowsAsync<ArgumentException>(() => _manager.SaveExternalAddressAsync(
            new NetworkStorageExternalAddressDto { AccessId = access.Id, AuthMode = mode, BaseUrl = baseUrl, Secret = secret },
            CancellationToken.None));
    }

    [Fact]
    public async Task GetStoragesAsync_CountsEpisodesByState()
    {
        await AddAccessAsync("media");
        var declared = AddItem("/tv/Show/S01E01.strm", "s3://media/Show/S01E01.mkv", size: 1000);
        AddItem("/tv/Show/S01E02.strm", "s3://media/Show/S01E02.mkv");
        var failed = AddItem("/tv/Show/S01E03.strm", "s3://media/Show/S01E03.mkv", size: 3000);
        AddItem("/tv/Other/S01E01.strm", "s3://nobody/Other/S01E01.mkv");
        AddItem("/tv/Web/S01E01.strm", "https://cdn.example.com/S01E01.mkv");
        AddItem("/tv/Web/S01E02.strm", null);
        AddItem("/tv/Show/Season 1", null, type: typeof(Season).FullName!);
        DeclareTrueCache(declared, failed);
        AddFailure(failed, "s3://media/Show/S01E03.mkv", NetworkStorageIssue.NotFound);

        var storages = await _manager.GetStoragesAsync(CancellationToken.None);

        var media = Assert.Single(storages, s => s.Key == "s3:media");
        Assert.Equal(3, media.ResourceCount);
        Assert.Equal(2, media.ValidCount);
        Assert.Equal(1, media.InvalidCount);
        Assert.Equal(4000, media.TotalSize);
        Assert.Equal(1, media.UndeclaredSizeCount);
        Assert.NotNull(media.AccessId);

        var nobody = Assert.Single(storages, s => s.Key == "s3:nobody");
        Assert.Equal(1, nobody.InvalidCount);
        Assert.Null(nobody.AccessId);

        Assert.Equal(1, Assert.Single(storages, s => s.Key == "http:cdn.example.com").UnknownCount);
        Assert.Equal(1, Assert.Single(storages, s => s.Key == NetworkStorageManager.UnresolvedStorageKey).UnknownCount);
    }

    [Fact]
    public async Task GetResourcesAsync_FiltersByStateAndPages()
    {
        await AddAccessAsync("media");
        var failed = AddItem("/tv/Show/S01E01.strm", "s3://media/Show/S01E01.mkv", size: 1000);
        AddItem("/tv/Show/S01E02.strm", "s3://media/Show/S01E02.mkv");
        AddItem("/tv/Show/S01E03.strm", "s3://media/Show/S01E03.mkv");
        DeclareTrueCache(failed);
        AddFailure(failed, "s3://media/Show/S01E01.mkv", NetworkStorageIssue.Forbidden);

        var invalid = await _manager.GetResourcesAsync("s3:media", [NetworkStorageResourceState.Invalid, NetworkStorageResourceState.Unknown], 0, 10, CancellationToken.None);
        var page = await _manager.GetResourcesAsync("s3:media", [], 1, 1, CancellationToken.None);

        var resource = Assert.Single(invalid.Items);
        Assert.Equal(failed, resource.ItemId);
        Assert.Equal(NetworkStorageIssue.Forbidden, resource.Issue);
        Assert.Equal(3, page.TotalRecordCount);
        Assert.Single(page.Items);
        Assert.Equal(NetworkStorageResourceState.Valid, page.Items[0].State);
    }

    [Theory]
    [InlineData(ObjectCheckStatus.NotFound, true)]
    [InlineData(ObjectCheckStatus.Forbidden, true)]
    [InlineData(ObjectCheckStatus.Transient, false)]
    [InlineData(ObjectCheckStatus.Ok, false)]
    public async Task ReportAccessFailureAsync_RecordsOnlyMissingOrForbiddenResources(ObjectCheckStatus status, bool recorded)
    {
        await AddAccessAsync("media");
        var item = AddItem("/tv/Show/S01E01.strm", "s3://media/Show/S01E01.mkv");
        _s3.Result = new ObjectCheckResult(status, 0, null);

        await _manager.ReportAccessFailureAsync(item, NetworkStorageFailureSource.Playback, null, CancellationToken.None);

        using var context = CreateDbContext();
        Assert.Equal(recorded, context.NetworkStorageFailures.Any(f => f.ItemId.Equals(item)));
    }

    [Fact]
    public async Task CheckAllAsync_ReplacesTheFailuresRecordedBefore()
    {
        await AddAccessAsync("media");
        var fixedItem = AddItem("/tv/Show/S01E01.strm", "s3://media/Show/S01E01.mkv");
        var missing = AddItem("/tv/Show/S01E02.strm", "s3://media/Show/S01E02.mkv");
        AddItem("/tv/Other/S01E01.strm", "s3://nobody/Other/S01E01.mkv");
        AddFailure(fixedItem, "s3://media/Show/S01E01.mkv", NetworkStorageIssue.NotFound);
        _s3.ResultFor = location => location.ObjectPath.EndsWith("S01E02.mkv", StringComparison.Ordinal)
            ? new ObjectCheckResult(ObjectCheckStatus.NotFound, 404, null)
            : new ObjectCheckResult(ObjectCheckStatus.Ok, 200, 10);

        await _manager.CheckAllAsync(new Progress<double>(), CancellationToken.None);

        using var context = CreateDbContext();
        var failure = Assert.Single(context.NetworkStorageFailures);
        Assert.Equal(missing, failure.ItemId);
        Assert.Equal((int)NetworkStorageFailureSource.Check, failure.Source);

        // A bucket without an access is not read.
        Assert.Equal(2, _s3.Checked);
    }

    [Fact]
    public async Task CheckAllAsync_FailureReportedWhileItRuns_IsReplacedByTheCheck()
    {
        await AddAccessAsync("media");
        var missing = AddItem("/tv/Show/S01E01.strm", "s3://media/Show/S01E01.mkv");
        _s3.ResultFor = _ =>
        {
            // Playback reports the resource after the check cleared the failures, before it records its own.
            AddFailure(missing, "s3://media/Show/S01E01.mkv", NetworkStorageIssue.Forbidden);
            return new ObjectCheckResult(ObjectCheckStatus.NotFound, 404, null);
        };

        await _manager.CheckAllAsync(new Progress<double>(), CancellationToken.None);

        using var context = CreateDbContext();
        var failure = Assert.Single(context.NetworkStorageFailures);
        Assert.Equal((int)NetworkStorageIssue.NotFound, failure.Issue);
        Assert.Equal((int)NetworkStorageFailureSource.Check, failure.Source);
    }

    [Fact]
    public async Task GetResourcesAsync_FailureOfAnEarlierTarget_IsIgnored()
    {
        await AddAccessAsync("media");
        var item = AddItem("/movies/Film.strm", "s3://media/Film-fixed.mkv", size: 1000);
        DeclareTrueCache(item);
        AddFailure(item, "s3://media/Film-missing.mkv", NetworkStorageIssue.NotFound);

        var resource = Assert.Single((await _manager.GetResourcesAsync("s3:media", [], 0, null, CancellationToken.None)).Items);

        Assert.Equal(NetworkStorageResourceState.Valid, resource.State);
        Assert.Null(resource.Issue);
        Assert.Null(resource.DateLastFailed);
    }

    [Fact]
    public async Task CheckAllAsync_DeclaredBeforeRefresh_ExpectsTheDeclaredSize()
    {
        await AddAccessAsync("media");
        // Still stored with the size of the text file.
        AddItem("/movies/Film.strm", "s3://media/Film.mkv", size: 30);
        DeclareTrueCache("/movies/Film.strm", 5000);
        long? expected = null;
        _s3.ResultFor = _ => new ObjectCheckResult(ObjectCheckStatus.Ok, 200, 5000);
        _s3.OnCheck = size => expected = size;

        await _manager.CheckAllAsync(new Progress<double>(), CancellationToken.None);

        Assert.Equal(5000, expected);
        var resource = Assert.Single((await _manager.GetResourcesAsync("s3:media", [], 0, null, CancellationToken.None)).Items);
        Assert.Equal(5000, resource.Size);
    }

    [Fact]
    public async Task ReportAccessFailureAsync_WithTarget_ChecksThatTarget()
    {
        await AddAccessAsync("media");
        var item = AddItem("/movies/Film.strm", "s3://media/Film-old.mkv");
        _s3.ResultFor = location => location.ObjectPath.EndsWith("Film-new.mkv", StringComparison.Ordinal)
            ? new ObjectCheckResult(ObjectCheckStatus.NotFound, 404, null)
            : new ObjectCheckResult(ObjectCheckStatus.Ok, 200, null);

        await _manager.ReportAccessFailureAsync(item, NetworkStorageFailureSource.Probe, "s3://media/Film-new.mkv", CancellationToken.None);

        using var context = CreateDbContext();
        Assert.Equal("s3://media/Film-new.mkv", Assert.Single(context.NetworkStorageFailures).Target);
    }

    private Task<NetworkStorageAccessDto> AddAccessAsync(string bucket, bool isArchive = false)
        => _manager.SaveAccessAsync(
            new NetworkStorageAccessDto { Bucket = bucket, Endpoint = "http://minio.lan:9000", UsePathStyle = true, AccessKey = "key", SecretKey = "secret", IsArchive = isArchive },
            CancellationToken.None);

    private Guid AddItem(string path, string? target, long? size = null, string? type = null)
    {
        var id = Guid.NewGuid();
        using var context = CreateDbContext();
        context.BaseItems.Add(new BaseItemEntity
        {
            Id = id,
            Type = type ?? typeof(Episode).FullName!,
            Path = path,
            Name = System.IO.Path.GetFileNameWithoutExtension(path),
            Size = size ?? 4,
            Data = target is null ? "{}" : JsonSerializer.Serialize(new { ShortcutPath = target })
        });
        context.SaveChanges();
        return id;
    }

    // Declares the items with the size they are stored with as the size of their media.
    private void DeclareTrueCache(params Guid[] ids)
    {
        using var context = CreateDbContext();
        foreach (var row in context.BaseItems.Where(e => ids.Contains(e.Id)).Select(e => new { e.Path, e.Size }).ToList())
        {
            DeclareTrueCache(row.Path!, row.Size);
        }
    }

    private void DeclareTrueCache(string path, long? mediaSize)
    {
        // The declared size of a .strm is the size of its target.
        var manifest = new TrueCacheManifest { Kind = TrueCacheKind.Video, SourceSize = mediaSize };
        var entry = new TrueCacheEntry(path, path + ".entry", "hash", manifest, null, null, null, new Dictionary<ImageType, string>(), new Dictionary<int, string>());
        _trueCache.Setup(t => t.Lookup(path)).Returns(TrueCacheLookup.Valid(entry));
    }

    private void AddFailure(Guid itemId, string target, NetworkStorageIssue issue)
    {
        using var context = CreateDbContext();
        context.NetworkStorageFailures.Add(new NetworkStorageFailure
        {
            ItemId = itemId,
            StorageKey = "s3:" + new Uri(target).Host,
            Target = target,
            Issue = (int)issue,
            StatusCode = 404,
            DateFirstFailed = DateTime.UtcNow,
            DateLastFailed = DateTime.UtcNow,
            Count = 1
        });
        context.SaveChanges();
    }

    private JellyfinDbContext CreateDbContext()
        => new(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(Mock.Of<IApplicationPaths>(), NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    // The S3 protocol, with the reads of resources answered by the test.
    private sealed class CheckingS3Protocol : INetworkStorageProtocol
    {
        private readonly S3StorageProtocol _inner = new(null!);
        private int _checked;

        public ObjectCheckResult Result { get; set; } = new(ObjectCheckStatus.Ok, 200, null);

        public Func<StorageLocation, ObjectCheckResult>? ResultFor { get; set; }

        public Action<long?>? OnCheck { get; set; }

        public int Checked => _checked;

        public string Protocol => _inner.Protocol;

        public bool RequiresAccess => _inner.RequiresAccess;

        public bool TryLocate(string target, [NotNullWhen(true)] out StorageLocation? location) => _inner.TryLocate(target, out location);

        public bool Covers(NetworkStorageAccessDto access, string container) => _inner.Covers(access, container);

        public Uri? GetInternalAddress(NetworkStorageAccessDto? access, StorageLocation location, DateTime now, string method)
            => _inner.GetInternalAddress(access, location, now, method);

        public Uri? GetExternalAddress(NetworkStorageAccessDto? access, NetworkStorageExternalAddressDto? external, StorageLocation location, DateTime now)
            => _inner.GetExternalAddress(access, external, location, now);

        public Task<NetworkStorageTestResult> TestAsync(NetworkStorageAccessDto access, CancellationToken cancellationToken)
            => Task.FromResult(new NetworkStorageTestResult(true, 200, null));

        public Task<ObjectCheckResult> CheckAsync(NetworkStorageAccessDto? access, StorageLocation location, long? expectedSize, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _checked);
            OnCheck?.Invoke(expectedSize);
            return Task.FromResult(ResultFor?.Invoke(location) ?? Result);
        }
    }
}
