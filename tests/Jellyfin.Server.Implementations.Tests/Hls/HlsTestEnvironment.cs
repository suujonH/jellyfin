using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.MediaEncoding.Hls.Published;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Hls;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Hls;

/// <summary>
/// The data of pre-encoded HLS in an in-memory database, the settings, and a local HLS store in a temporary directory.
/// </summary>
internal sealed class HlsTestEnvironment : IDisposable
{
    public const string Profile = HlsEncodingProfile.Version;

    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    // What the verification passes to PublishAsync: the lifetime of a playback grant.
    public static readonly TimeSpan ReplacedGrace = TimeSpan.FromHours(24);

    public static readonly int[] Qualities = [1080, 720];

    public static readonly Guid LibraryId = Guid.Parse("0123456789abcdef0123456789abcdef");

    public static readonly Guid OtherLibraryId = Guid.Parse("fedcba9876543210fedcba9876543210");

    public static readonly DateTime Modified = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    // One audio track (index 1), one text subtitle (index 2) and one graphic subtitle (index 3, never planned); 60 seconds, so 10 segments.
    public static readonly HlsProbe Probe = new(
        1920,
        1080,
        25,
        60,
        "h264",
        "yuv420p",
        "bt709",
        "progressive",
        0,
        [new(1, "aac", 6, 48000, "eng", string.Empty, true)],
        [new(2, "subrip", "eng", string.Empty, false), new(3, "hdmv_pgs_subtitle", "eng", string.Empty, false)]);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly Mock<IConfigurationManager> _configurationManager = new();
    private HlsOptions _options;

    public HlsTestEnvironment(IHlsStoreFactory? storeFactory = null)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options;
        using (var context = CreateDbContext())
        {
            context.Database.EnsureCreated();
        }

        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateDbContext);
        Store = new HlsJobStore(factory.Object);

        Directory.CreateDirectory(TempDirectory);
        _options = new HlsOptions { Enabled = true, Qualities = Qualities };
        _configurationManager.Setup(m => m.GetConfiguration(HlsConfigurationStore.StoreKey)).Returns(() => _options);
        Settings = new HlsSettings(_configurationManager.Object, NullLogger<HlsSettings>.Instance);
        StoreFactory = storeFactory ?? new HlsStoreFactory(Mock.Of<INetworkStorageManager>());
        Monitor = new HlsRootMonitor(Store, StoreFactory, Settings, NullLogger<HlsRootMonitor>.Instance);
        Index = new HlsPublishedIndex(Store);
    }

    public HlsJobStore Store { get; }

    public HlsSettings Settings { get; }

    public IHlsStoreFactory StoreFactory { get; }

    public HlsRootMonitor Monitor { get; }

    public HlsPublishedIndex Index { get; }

    public string TempDirectory { get; } = Path.Combine(Path.GetTempPath(), "jellyfin-hls-tests", Guid.NewGuid().ToString("N"));

    // The local HLS store the worker reports; it does not exist until a test creates it.
    public string Root => Path.Combine(TempDirectory, "hls");

    public DateTime Now { get; set; } = new(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public static string Sha(char c) => new(c, 64);

    // What a worker reports: every rendition of the plan with the files and segment durations it should have.
    public static HlsRenditionOutput[] Outputs(HlsEncodePlan plan)
    {
        HlsOutputFile[] segments = [new(HlsRenditionPaths.InitFile, 900), .. Enumerable.Range(0, plan.ExpectedSegments).Select(i => new HlsOutputFile(HlsRenditionPaths.GetSegmentFile(i), 1000))];
        double[] durations = [.. Enumerable.Repeat(plan.SegmentSeconds, plan.ExpectedSegments)];
        return
        [
            .. plan.Renditions.Select(r => r.Kind switch
            {
                HlsRenditionKind.Video => new HlsRenditionOutput(
                    r.Key,
                    new HlsRenditionSummary(r.Video!.Name, r.Video.Width, r.Video.Height, r.Video.FramesPerSecond, 5_000_000, 4_000_000, "avc1.640028"),
                    segments,
                    durations),
                HlsRenditionKind.Audio => new HlsRenditionOutput(r.Key, new HlsRenditionSummary(SourceIndex: r.SourceIndex, Channels: r.Channels, Bandwidth: 192_000), segments, durations),
                HlsRenditionKind.Subtitle => new HlsRenditionOutput(r.Key, new HlsRenditionSummary(SourceIndex: r.SourceIndex), [new HlsOutputFile(HlsRenditionPaths.SubtitleFile, 100)], []),
                _ => new HlsRenditionOutput(r.Key, new HlsRenditionSummary(), [new HlsOutputFile(HlsRenditionPaths.PreviewFile, 100)], [])
            })
        ];
    }

    public static HlsSourceFile LibraryFile(Guid item, Guid? libraryId = null)
        => new(item, libraryId ?? LibraryId, "/media/" + item.ToString("N", CultureInfo.InvariantCulture) + ".mkv", 1000, Modified);

    // Saves settings, as the configuration manager does.
    public void Save(HlsOptions options)
    {
        _options = options;
        RaiseUpdated(HlsConfigurationStore.StoreKey, options);
    }

    public void RaiseUpdated(string key, object configuration)
        => _configurationManager.Raise(m => m.NamedConfigurationUpdated += null, new ConfigurationUpdateEventArgs(key, configuration));

    // Records a new library file; its source job is created a second after every earlier one, so jobs are claimed in this order.
    public async Task<Guid> AddSourceAsync(Guid? libraryId = null)
    {
        var item = Guid.NewGuid();
        Now += TimeSpan.FromSeconds(1);
        var result = Assert.Single(await Store.UpsertSourcesAsync([LibraryFile(item, libraryId)], Now, CancellationToken));
        Assert.Equal(HlsSourceChange.Inserted, result.Change);
        return item;
    }

    // Claims the oldest pending job and identifies its file as the content, which it must then encode.
    public async Task<(HlsJobLease Lease, HlsEncodePlan Plan)> ClaimAndPlanAsync(string sha, string? root = null)
    {
        var lease = await Store.ClaimAsync(new HlsClaimRequest("worker", Lease, _ => true, Now), CancellationToken);
        Assert.NotNull(lease);
        var result = await Store.IdentifyAsync(new HlsIdentifyRequest(lease.Id, lease.LeaseToken, sha, Probe, root ?? Root, _ => Qualities, Profile, _ => true, Now), CancellationToken);
        Assert.Equal(HlsIdentifyOutcome.Planned, result.Outcome);
        return (lease, result.Plan!);
    }

    // A new library file identified as the content, whose outputs its source job submitted; their files are written to the store.
    public async Task<(Guid Item, HlsJobLease Lease, HlsEncodePlan Plan)> SubmitAsync(string sha, string? root = null)
    {
        var item = await AddSourceAsync();
        var (lease, plan) = await ClaimAndPlanAsync(sha, root);
        Assert.Equal(item, lease.Input!.ItemId);
        WriteFiles(plan, root);
        Assert.True(await Store.SubmitOutputsAsync(lease.Id, lease.LeaseToken, Outputs(plan), Now, CancellationToken));
        return (item, lease, plan);
    }

    // The same, published right away.
    public async Task<(Guid Item, HlsEncodePlan Plan)> PublishAsync(string sha)
    {
        var (item, lease, plan) = await SubmitAsync(sha);
        Assert.NotNull(await Store.PublishAsync(lease.Id, ReplacedGrace, Now, CancellationToken));
        return (item, plan);
    }

    // Writes the files a worker uploads for the plan.
    public void WriteFiles(HlsEncodePlan plan, string? root = null)
    {
        var renditions = plan.Renditions.ToDictionary(r => r.Key, r => r.RelativeDirectory, StringComparer.Ordinal);
        foreach (var output in Outputs(plan))
        {
            var directory = GetStorePath(renditions[output.Key], root);
            Directory.CreateDirectory(directory);
            foreach (var file in output.Files)
            {
                File.WriteAllBytes(Path.Combine(directory, file.Path), new byte[file.Length]);
            }
        }
    }

    public async Task<HlsJob> JobAsync(Guid id)
    {
        await using var context = CreateDbContext();
        return await context.HlsJobs.AsNoTracking().SingleAsync(j => j.Id.Equals(id), CancellationToken);
    }

    public async Task<List<HlsJob>> JobsAsync()
    {
        await using var context = CreateDbContext();
        return await context.HlsJobs.AsNoTracking().OrderBy(j => j.DateCreated).ThenBy(j => j.WorkKey).ToListAsync(CancellationToken);
    }

    public async Task<List<HlsDeletion>> DeletionsAsync()
    {
        await using var context = CreateDbContext();
        return await context.HlsDeletions.AsNoTracking().OrderBy(d => d.RelativeDirectory).ToListAsync(CancellationToken);
    }

    // A table that cannot be read until it is given back its name: what a transient database failure looks like.
    public async Task RenameTableAsync(string table, string name)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = $"ALTER TABLE \"{table}\" RENAME TO \"{name}\"";
        await command.ExecuteNonQueryAsync(CancellationToken);
    }

    public string GetStorePath(string relativeDirectory, string? root = null) => Path.Combine(root ?? Root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));

    public JellyfinDbContext CreateDbContext()
        => new(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(Mock.Of<IApplicationPaths>(), NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));

    public void Dispose()
    {
        Index.Dispose();
        Settings.Dispose();
        Store.Dispose();
        _connection.Dispose();
        Directory.Delete(TempDirectory, true);
    }
}
