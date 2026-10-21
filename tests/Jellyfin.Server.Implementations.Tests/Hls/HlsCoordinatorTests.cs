using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Hls;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Server.Implementations.Tests.Hls.HlsTestEnvironment;

namespace Jellyfin.Server.Implementations.Tests.Hls;

public sealed class HlsCoordinatorTests : IDisposable
{
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromMinutes(10);

    private readonly IMediaSourceManager _previousMediaSourceManager = BaseItem.MediaSourceManager;
    private readonly HlsTestEnvironment _environment = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
    private readonly Mock<ITrueCacheService> _trueCache = new();
    private readonly List<Video> _items = [];
    private readonly Dictionary<Guid, Guid[]> _libraries = [];
    private readonly HashSet<Guid> _broken = [];
    private readonly HlsCoordinator _coordinator;
    private readonly DateTime _start;

    public HlsCoordinatorTests()
    {
        // Video.IsFileProtocol asks the media source manager.
        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager.Setup(m => m.GetPathProtocol(It.IsAny<string>()))
            .Returns((string path) => path.Contains("://", StringComparison.Ordinal) ? MediaProtocol.Http : MediaProtocol.File);
        BaseItem.MediaSourceManager = mediaSourceManager.Object;

        _libraryManager.Setup(m => m.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(() => _items.Cast<BaseItem>().ToList());
        _libraryManager.Setup(m => m.GetItemById(It.IsAny<Guid>()))
            .Returns((Guid id) => _broken.Contains(id) ? throw new IOException("The database is busy.") : _items.FirstOrDefault(i => i.Id.Equals(id)));
        _libraryManager.Setup(m => m.GetCollectionFolders(It.IsAny<BaseItem>()))
            .Returns((BaseItem item) => _libraries.TryGetValue(item.Id, out var libraries) ? libraries.Select(id => new Folder { Id = id }).ToList() : []);
        _trueCache.Setup(t => t.Lookup(It.IsAny<string>())).Returns(TrueCacheLookup.None);

        // An s3:// target is signed for the bucket that has an access, others are returned as they are.
        var networkStorage = new Mock<INetworkStorageManager>();
        networkStorage.Setup(n => n.ResolveShortcutTarget(It.IsAny<string>()))
            .Returns((string target) => target.StartsWith("s3://bucket/", StringComparison.Ordinal)
                ? "https://storage.example/" + target[5..] + "?X-Amz-Signature=0"
                : target.StartsWith("s3://", StringComparison.Ordinal) ? null : target);

        _coordinator = new HlsCoordinator(_environment.Store, _environment.Settings, _environment.Index, _libraryManager.Object, networkStorage.Object, _trueCache.Object, NullLogger<HlsCoordinator>.Instance);
        _start = _environment.Now;
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _coordinator.Dispose();
        _environment.Dispose();
        BaseItem.MediaSourceManager = _previousMediaSourceManager;
    }

    [Fact]
    public async Task FirstTick_RecordsTheFilesOfTheEnabledLibraries()
    {
        _environment.Save(Options(libraries: [LibraryId]));
        var local = AddLocal();
        var sized = AddLocal(size: 5000, modified: Modified.ToLocalTime());
        var shortcut = AddShortcut("s3://bucket/a.mkv", 7000);

        // Its folder is in a library that is not selected first.
        var shared = AddLocal();
        _libraries[shared.Id] = [OtherLibraryId, LibraryId];

        // Not pre-encoded: another library, a disc image, a .strm file of unknown size, of a bucket without an access or
        // naming a local file, which a .strm file may not.
        AddLocal(library: OtherLibraryId);
        AddLocal(type: VideoType.Iso);
        AddShortcut("s3://bucket/b.mkv", null);
        AddShortcut("s3://other/c.mkv", 100);
        AddShortcut("/srv/d.mkv", 100);

        await TickAsync(_start);

        var sources = (await _environment.Store.GetSourcesAsync(CancellationToken)).ToDictionary(s => s.File.ItemId, s => s.File);
        Assert.Equal(new[] { local.Id, sized.Id, shortcut.Id, shared.Id }.Order(), sources.Keys.Order());
        Assert.Equal(new HlsSourceFile(local.Id, LibraryId, local.Path, 123, Modified), sources[local.Id]);
        Assert.Equal(new HlsSourceFile(sized.Id, LibraryId, sized.Path, 5000, Modified), sources[sized.Id]);
        Assert.Equal(new HlsSourceFile(shortcut.Id, LibraryId, "s3://bucket/a.mkv", 7000, Modified), sources[shortcut.Id]);
        Assert.Equal(LibraryId, sources[shared.Id].LibraryId);

        // Each waits for a worker to identify it, and playback knows it.
        Assert.Equal(sources.Keys.Order(), (await _environment.JobsAsync()).Where(j => j.Status == (int)HlsJobStatus.Pending).Select(j => j.ItemId!.Value).Order());
        Assert.NotNull(_environment.Index.GetSource(shortcut.Id));
    }

    [Fact]
    public async Task LibraryEvents_AreRecordedOnTheNextTick()
    {
        var removed = AddLocal();
        var updated = AddLocal();
        await TickAsync(_start);

        var added = AddLocal();
        _libraryManager.Raise(m => m.ItemAdded += null, _libraryManager.Object, new ItemChangeEventArgs { Item = added });
        _items.Remove(removed);
        _libraryManager.Raise(m => m.ItemRemoved += null, _libraryManager.Object, new ItemChangeEventArgs { Item = removed });

        // An item that is no longer a file the worker can read is no longer a source either.
        updated.VideoType = VideoType.BluRay;
        _libraryManager.Raise(m => m.ItemUpdated += null, _libraryManager.Object, new ItemChangeEventArgs { Item = updated });
        await TickAsync(_start + TimeSpan.FromSeconds(5));

        Assert.Equal([added.Id], (await _environment.Store.GetSourcesAsync(CancellationToken)).Select(s => s.File.ItemId));
        Assert.NotNull(_environment.Index.GetSource(added.Id));
        Assert.Null(_environment.Index.GetSource(removed.Id));
        Assert.Null(_environment.Index.GetSource(updated.Id));
    }

    [Fact]
    public async Task ChangingTheLibraries_ScansAgain()
    {
        _environment.Save(Options(libraries: [LibraryId]));
        var first = AddLocal();
        var other = AddLocal(library: OtherLibraryId);
        await TickAsync(_start);
        Assert.Equal([first.Id], await SourceItemsAsync());

        _environment.Save(Options());
        await TickAsync(_start + TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { first.Id, other.Id }.Order(), await SourceItemsAsync());

        _environment.Save(Options(libraries: [OtherLibraryId]));
        await TickAsync(_start + TimeSpan.FromSeconds(10));
        Assert.Equal([other.Id], await SourceItemsAsync());
    }

    [Fact]
    public async Task ChangingTheQualities_SyncsTheRenditionsOfEveryContent_AndSchedulesTheNewOnes()
    {
        await TickAsync(_start);
        var (item, plan) = await _environment.PublishAsync(Sha('a'));
        AddKnown(item);
        await _environment.Index.LoadAsync(CancellationToken);

        _environment.Save(Options(qualities: [720]));
        await TickAsync(_start + TimeSpan.FromSeconds(5));

        Assert.Equal(["a1", "preview", "s2", "v720"], (await _environment.Store.GetRenditionsAsync(Sha('a'), CancellationToken)).Select(r => r.Key));
        Assert.Equal([plan.Renditions.Single(r => r.Key == "v1080").RelativeDirectory], (await _environment.DeletionsAsync()).Select(d => d.RelativeDirectory));
        Assert.Equal(["v720"], _environment.Index.GetMediaForItem(item)!.Video.Select(r => r.Key));

        _environment.Save(Options(qualities: [720, 480]));
        await TickAsync(_start + TimeSpan.FromSeconds(10));

        var v480 = (await _environment.Store.GetRenditionsAsync(Sha('a'), CancellationToken)).Single(r => r.Key == "v480");
        var job = await _environment.JobAsync(v480.JobId!.Value);
        Assert.Equal((HlsJobKind.Media, HlsJobStatus.Pending, item), ((HlsJobKind)job.Kind, (HlsJobStatus)job.Status, job.ItemId));
    }

    [Fact]
    public async Task TurningItOff_CancelsTheWorkList_AndTurningItOnAgainScans()
    {
        var first = AddLocal();
        await TickAsync(_start);

        _environment.Save(Options(enabled: false));
        await TickAsync(_start + TimeSpan.FromSeconds(5));

        // The files stay recorded; what changes in the library while it is off waits.
        Assert.All(await _environment.JobsAsync(), j => Assert.Equal((int)HlsJobStatus.Cancelled, j.Status));
        var second = AddLocal();
        _libraryManager.Raise(m => m.ItemAdded += null, _libraryManager.Object, new ItemChangeEventArgs { Item = second });
        await TickAsync(_start + TimeSpan.FromSeconds(10));
        Assert.Equal([first.Id], await SourceItemsAsync());

        _environment.Save(Options());
        await TickAsync(_start + TimeSpan.FromSeconds(15));

        Assert.Equal(new[] { first.Id, second.Id }.Order(), await SourceItemsAsync());
        Assert.Equal(
            new[] { first.Id, second.Id }.Order(),
            (await _environment.JobsAsync()).Where(j => j.Status == (int)HlsJobStatus.Pending).Select(j => j.ItemId!.Value).Order());
    }

    [Fact]
    public async Task Rebuild_MakesFailedRenditionsMissing_AndSchedulesThem()
    {
        await TickAsync(_start);
        var item = await _environment.AddSourceAsync();
        AddKnown(item);
        var (lease, _) = await _environment.ClaimAndPlanAsync(Sha('a'));
        Assert.True(await _environment.Store.ReleaseAsync(lease.Id, lease.LeaseToken, "ffmpeg failed", 1, Lease, _environment.Now, CancellationToken));
        Assert.All(await _environment.Store.GetRenditionsAsync(Sha('a'), CancellationToken), r => Assert.Equal(HlsRenditionState.Failed, r.State));

        _coordinator.RequestRebuild();
        await TickAsync(_start + TimeSpan.FromSeconds(5));

        var renditions = await _environment.Store.GetRenditionsAsync(Sha('a'), CancellationToken);
        Assert.All(renditions, r => Assert.Equal(HlsRenditionState.Missing, r.State));
        var job = await _environment.JobAsync(Assert.Single(renditions.Select(r => r.JobId).Distinct())!.Value);
        Assert.Equal((HlsJobKind.Media, HlsJobStatus.Pending, item), ((HlsJobKind)job.Kind, (HlsJobStatus)job.Status, job.ItemId));
    }

    [Fact]
    public async Task Sweep_SchedulesTheContentsWhoseRenditionsLostTheirJob_EveryTenMinutes()
    {
        await TickAsync(_start);
        var item = await _environment.AddSourceAsync();
        AddKnown(item);
        await _environment.ClaimAndPlanAsync(Sha('a'));
        await _environment.Store.CancelAllJobsAsync(_environment.Now, CancellationToken);
        await _environment.Index.LoadAsync(CancellationToken);

        await TickAsync(_start + _sweepInterval - TimeSpan.FromSeconds(1));
        Assert.All(await _environment.Store.GetRenditionsAsync(Sha('a'), CancellationToken), r => Assert.Null(r.JobId));

        await TickAsync(_start + _sweepInterval);

        // The job reads the file of an item the index knows.
        var renditions = await _environment.Store.GetRenditionsAsync(Sha('a'), CancellationToken);
        var job = await _environment.JobAsync(Assert.Single(renditions.Select(r => r.JobId).Distinct())!.Value);
        Assert.Equal((HlsJobKind.Media, item), ((HlsJobKind)job.Kind, job.ItemId));
    }

    [Fact]
    public async Task AFailedLibraryChange_IsRetriedByTheSweep()
    {
        await TickAsync(_start);
        var added = AddLocal();
        _broken.Add(added.Id);
        _libraryManager.Raise(m => m.ItemAdded += null, _libraryManager.Object, new ItemChangeEventArgs { Item = added });
        await TickAsync(_start + TimeSpan.FromSeconds(5));
        _broken.Clear();
        await TickAsync(_start + TimeSpan.FromSeconds(10));
        Assert.Empty(await SourceItemsAsync());

        // The sweep queues it for the next tick.
        await TickAsync(_start + _sweepInterval);
        await TickAsync(_start + _sweepInterval + TimeSpan.FromSeconds(5));

        Assert.Equal([added.Id], await SourceItemsAsync());
    }

    [Fact]
    public async Task AFailedRemovalOfAScan_IsRetriedByTheSweep()
    {
        var gone = AddLocal();
        await TickAsync(_start);
        _items.Remove(gone);

        // Removing a file cancels its job, which cannot be done while the jobs cannot be read.
        _environment.Save(Options(libraries: [LibraryId]));
        await _environment.RenameTableAsync("HlsJobs", "HlsJobsGone");
        await TickAsync(_start + TimeSpan.FromSeconds(5));
        await _environment.RenameTableAsync("HlsJobsGone", "HlsJobs");
        await TickAsync(_start + TimeSpan.FromSeconds(10));
        Assert.Equal([gone.Id], await SourceItemsAsync());

        await TickAsync(_start + _sweepInterval);
        await TickAsync(_start + _sweepInterval + TimeSpan.FromSeconds(5));

        Assert.Empty(await SourceItemsAsync());
    }

    [Fact]
    public async Task AFailedRenditionSync_IsRetriedByTheSweep()
    {
        await TickAsync(_start);
        var (item, plan) = await _environment.PublishAsync(Sha('a'));
        AddKnown(item);
        await _environment.Index.LoadAsync(CancellationToken);
        var probe = await SetProbeAsync(Sha('a'), "{");

        _environment.Save(Options(qualities: [720]));
        await TickAsync(_start + TimeSpan.FromSeconds(5));
        await SetProbeAsync(Sha('a'), probe);
        await TickAsync(_start + TimeSpan.FromSeconds(10));
        Assert.Contains("v1080", (await _environment.Store.GetRenditionsAsync(Sha('a'), CancellationToken)).Select(r => r.Key));

        await TickAsync(_start + _sweepInterval);

        Assert.Equal(["a1", "preview", "s2", "v720"], (await _environment.Store.GetRenditionsAsync(Sha('a'), CancellationToken)).Select(r => r.Key));
        Assert.Equal([plan.Renditions.Single(r => r.Key == "v1080").RelativeDirectory], (await _environment.DeletionsAsync()).Select(d => d.RelativeDirectory));
        Assert.Equal(["v720"], _environment.Index.GetMediaForItem(item)!.Video.Select(r => r.Key));
    }

    [Fact]
    public async Task AnExpiredLease_KeepsTheDirectories_UntilTheUploadAddressesExpired()
    {
        await TickAsync(_start);
        await _environment.AddSourceAsync();
        var (_, plan) = await _environment.ClaimAndPlanAsync(Sha('a'));
        var expired = _environment.Now + Lease + TimeSpan.FromSeconds(1);

        await TickAsync(expired);

        var deletions = await _environment.DeletionsAsync();
        Assert.Equal(plan.Renditions.Select(r => r.RelativeDirectory).Order(StringComparer.Ordinal), deletions.Select(d => d.RelativeDirectory).Order(StringComparer.Ordinal));
        Assert.All(deletions, d => Assert.Equal(expired + TimeSpan.FromHours(1) + Lease, d.NotBefore));
    }

    [Fact]
    public async Task AContentWithoutAFile_IsDeletedAfterTheRetention()
    {
        _environment.Save(Options(orphanRetentionHours: 1));
        await TickAsync(_start);
        var (item, plan) = await _environment.PublishAsync(Sha('a'));
        var video = AddKnown(item);
        await _environment.Index.LoadAsync(CancellationToken);

        _items.Remove(video);
        _libraryManager.Raise(m => m.ItemRemoved += null, _libraryManager.Object, new ItemChangeEventArgs { Item = video });
        await TickAsync(_start + _sweepInterval);

        Assert.Null(_environment.Index.GetSource(item));
        Assert.NotNull(await _environment.Store.GetMediaAsync(Sha('a'), CancellationToken));
        Assert.Empty(await _environment.DeletionsAsync());

        await TickAsync(_start + _sweepInterval + TimeSpan.FromHours(1));

        Assert.Null(await _environment.Store.GetMediaAsync(Sha('a'), CancellationToken));
        Assert.Equal(plan.Renditions.Select(r => r.RelativeDirectory).Order(StringComparer.Ordinal), (await _environment.DeletionsAsync()).Select(d => d.RelativeDirectory));
    }

    private HlsOptions Options(bool enabled = true, Guid[]? libraries = null, int[]? qualities = null, int orphanRetentionHours = 72)
        => new()
        {
            Enabled = enabled,
            Store = _environment.Root,
            Libraries = libraries ?? [],
            Qualities = qualities ?? Qualities,
            OrphanRetentionHours = orphanRetentionHours
        };

    private Task TickAsync(DateTime now) => _coordinator.TickAsync(now, CancellationToken);

    private async Task<IEnumerable<Guid>> SourceItemsAsync()
        => (await _environment.Store.GetSourcesAsync(CancellationToken)).Select(s => s.File.ItemId).Order();

    private Movie AddLocal(Guid? library = null, long? size = null, DateTime? modified = null, VideoType type = VideoType.VideoFile)
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(_environment.TempDirectory, id.ToString("N", CultureInfo.InvariantCulture) + ".mkv");
        File.WriteAllBytes(path, new byte[123]);

        // A date read back from the database has no kind.
        return Add(new Movie { Id = id, Path = path, Size = size, VideoType = type, DateModified = modified ?? DateTime.SpecifyKind(Modified, DateTimeKind.Unspecified) }, library);
    }

    // The size of the target is what the entry declares; the item has the size of the text file.
    private Movie AddShortcut(string target, long? size)
    {
        var id = Guid.NewGuid();
        var path = Path.Combine(_environment.TempDirectory, id.ToString("N", CultureInfo.InvariantCulture) + ".strm");
        if (size is not null)
        {
            var manifest = new TrueCacheManifest { Kind = TrueCacheKind.Video, SourceSize = size };
            var entry = new TrueCacheEntry(path, path + ".cache", "hash", manifest, null, null, null, new Dictionary<ImageType, string>(), new Dictionary<int, string>());
            _trueCache.Setup(t => t.Lookup(path)).Returns(TrueCacheLookup.Valid(entry));
        }

        return Add(new Movie { Id = id, Path = path, IsShortcut = true, ShortcutPath = target, Size = 93, VideoType = VideoType.VideoFile, DateModified = Modified }, null);
    }

    // The library item of a file the test recorded itself.
    private Movie AddKnown(Guid item)
    {
        var file = LibraryFile(item);
        return Add(new Movie { Id = item, Path = file.Path, Size = file.Length, VideoType = VideoType.VideoFile, DateModified = file.DateModified }, null);
    }

    private Movie Add(Movie movie, Guid? library)
    {
        _items.Add(movie);
        _libraries[movie.Id] = [library ?? LibraryId];
        return movie;
    }

    // Returns the probe it replaces.
    private async Task<string> SetProbeAsync(string sha256, string probe)
    {
        await using var context = _environment.CreateDbContext();
        var media = (await context.HlsMedia.FindAsync([sha256], CancellationToken))!;
        var previous = media.Probe;
        media.Probe = probe;
        await context.SaveChangesAsync(CancellationToken);
        return previous;
    }
}
