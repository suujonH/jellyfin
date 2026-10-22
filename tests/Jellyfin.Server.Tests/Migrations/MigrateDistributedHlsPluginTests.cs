using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Migrations.Routines;
using Jellyfin.Server.ServerSetupApp;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Hls;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Tests.Migrations;

public sealed class MigrateDistributedHlsPluginTests : IDisposable
{
    // The tables of the plugin that are read.
    private const string Schema = """
        CREATE TABLE sources(item_id TEXT PRIMARY KEY, library_id TEXT NOT NULL, path TEXT NOT NULL, length INTEGER NOT NULL,
          modified_ticks INTEGER NOT NULL, sha256 TEXT NULL);
        CREATE TABLE media(sha256 TEXT PRIMARY KEY, media_uuid TEXT NOT NULL UNIQUE, library_id TEXT NOT NULL, probe TEXT NOT NULL,
          orphaned_at INTEGER NULL, created_at INTEGER NOT NULL);
        CREATE TABLE renditions(sha256 TEXT NOT NULL, key TEXT NOT NULL, state INTEGER NOT NULL,
          job_id TEXT NULL, published_job_id TEXT NULL, rel_dir TEXT NULL, summary TEXT NULL, profile TEXT NULL, published_at INTEGER NULL,
          PRIMARY KEY(sha256, key));
        CREATE TABLE job_outputs(job_id TEXT NOT NULL, key TEXT NOT NULL, rel_dir TEXT NOT NULL, summary TEXT NOT NULL, files TEXT NOT NULL,
          PRIMARY KEY(job_id, key));
        CREATE TABLE version_items(item_id TEXT NOT NULL, quality INTEGER NOT NULL, version_id TEXT NOT NULL, signature TEXT NOT NULL,
          PRIMARY KEY(item_id, quality));
        CREATE TABLE jobs(id TEXT PRIMARY KEY, work_key TEXT NOT NULL UNIQUE, kind INTEGER NOT NULL, item_id TEXT NULL, sha256 TEXT NULL,
          payload TEXT NOT NULL, status INTEGER NOT NULL, worker TEXT NULL, lease_token TEXT NULL, lease_until INTEGER NULL,
          attempt INTEGER NOT NULL DEFAULT 0, error TEXT NULL, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL);
        CREATE TABLE deletions(rel_dir TEXT PRIMARY KEY, not_before INTEGER NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
          reason TEXT NOT NULL, last_error TEXT NULL);
        """;

    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string MediaKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string PublishedDirectory = MediaKey + "/video/1080/11111111111111111111111111111111";
    private const string UnfinishedDirectory = MediaKey + "/video/720/22222222222222222222222222222222";
    private const string QueuedDirectory = MediaKey + "/video/480/33333333333333333333333333333333";
    private const string UploadingDirectory = MediaKey + "/audio/1/44444444444444444444444444444444";
    private const string SigningKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=";

    private static readonly Guid _libraryId = Guid.Parse("0123456789abcdef0123456789abcdef");
    private static readonly Guid _itemId = Guid.Parse("11111111222233334444555555555555");
    private static readonly Guid _publishingJob = Guid.Parse("99999999999999999999999999999999");
    private static readonly Guid _versionId = Guid.Parse("88888888888888888888888888888888");
    private static readonly DateTime _modified = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime _leaseUntil = new(2126, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly HlsProbe _probe = new(1920, 1080, 25, 60, "h264", "yuv420p", "bt709", "progressive", 0, [new(1, "aac", 6, 48000, "eng", string.Empty, true)], []);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly Mock<IConfigurationManager> _configurationManager = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "jellyfin-hls-migration-" + Guid.NewGuid().ToString("N"));
    private HlsOptions? _saved;

    public MigrateDistributedHlsPluginTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options;
        using var context = CreateDbContext();
        context.Database.EnsureCreated();
        Directory.CreateDirectory(ConfigurationDirectory);
        Directory.CreateDirectory(PluginConfigurationsDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        _configurationManager.Setup(c => c.SaveConfiguration(HlsConfigurationExtensions.ConfigurationKey, It.IsAny<object>()))
            .Callback<string, object>((_, options) => _saved = (HlsOptions)options);
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    private string ConfigurationDirectory => Path.Combine(_root, "config");

    private string PluginConfigurationsDirectory => Path.Combine(_root, "plugins", "configurations");

    private string DatabasePath => Path.Combine(_root, "data", "distributed-hls", "jobs.db");

    private string MappedDirectory => Path.Combine(_root, "mnt");

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, true);
    }

    [Fact]
    public async Task Perform_PluginWithData_ImportsWhatIsPublishedAndTheSettings()
    {
        var user = await AddUserAsync();
        await WritePluginAsync(Path.Combine(MappedDirectory, "hls"), user);
        await WriteS3StorageAsync();
        CreateDatabase();

        await CreateMigration().PerformAsync(CancellationToken);

        Assert.NotNull(_saved);
        Assert.True(_saved.Enabled);
        Assert.Equal([_libraryId], _saved.Libraries);
        Assert.Equal([1080, 720], _saved.Qualities);
        Assert.True(_saved.AllowOriginal);
        Assert.False(_saved.AllowLocalOriginal);
        Assert.Equal(7, _saved.LeaseMinutes);
        Assert.Equal(SigningKey, _saved.PlaybackSigningKey);

        const string Root = "s3://media-bucket/library/hls";
        using (var context = CreateDbContext())
        {
            Assert.True(context.Users.Include(u => u.Permissions).Single(u => u.Id.Equals(user)).HasPermission(PermissionKind.EnableOriginalPlayback));

            var media = Assert.Single(context.HlsMedia);
            Assert.Equal(MediaKey, media.MediaKey);
            Assert.Equal(JsonSerializer.Serialize(_probe), media.Probe);

            var sources = context.HlsSources.OrderBy(s => s.Path).ToList();
            Assert.Equal(2, sources.Count);
            Assert.Equal(Sha, sources[0].Sha256);
            Assert.Equal(_modified, sources[0].DateModified);
            Assert.Null(sources[1].Sha256);

            // Only what is published, without the segments, which are read when it is played.
            var rendition = Assert.Single(context.HlsRenditions);
            Assert.Equal("v1080", rendition.Key);
            Assert.Equal((int)HlsRenditionState.Published, rendition.State);
            Assert.Equal(_publishingJob, rendition.PublishedJobId);
            Assert.Equal(PublishedDirectory, rendition.RelativeDirectory);
            Assert.Equal(Root, rendition.Base);
            var output = Assert.Single(context.HlsJobOutputs);
            Assert.Equal("[]", output.Segments);
            Assert.Equal([new HlsOutputFile("init.mp4", 900)], JsonSerializer.Deserialize<HlsOutputFile[]>(output.Files)!);
            Assert.Empty(context.HlsJobs);

            // The queue of the plugin, the outputs of the job that did not finish, and the directories of the job still uploading,
            // one lease length after its lease.
            var deletions = context.HlsDeletions.OrderBy(d => d.RelativeDirectory).ToList();
            Assert.Equal([UploadingDirectory, QueuedDirectory, UnfinishedDirectory], deletions.Select(d => d.RelativeDirectory));
            Assert.All(deletions, d => Assert.Equal(Root, d.Base));
            Assert.Equal(_leaseUntil.AddMinutes(7), deletions[0].NotBefore);
            Assert.Equal(2, deletions[1].Attempts);

            var version = Assert.Single(context.HlsVersionItems);
            Assert.Equal(_versionId, version.VersionId);
            Assert.Empty(version.Signature);

            var access = Assert.Single(context.NetworkStorageAccesses);
            Assert.Equal("media-bucket", access.Bucket);
            Assert.Equal("http://minio.lan:9000", access.Endpoint);
            Assert.Equal("secret", access.SecretKey);
            Assert.True(access.UsePathStyle);
            Assert.False(access.IsArchive);
        }

        Assert.False(File.Exists(DatabasePath));
        Assert.True(File.Exists(DatabasePath + ".migrated"));
    }

    [Fact]
    public async Task Perform_HlsSettingsExist_KeepsThemAndImportsIntoTheLocalDirectory()
    {
        var user = await AddUserAsync();
        var output = Path.Combine(_root, "hls") + Path.DirectorySeparatorChar;
        await WritePluginAsync(output, user);
        await File.WriteAllTextAsync(Path.Combine(ConfigurationDirectory, "hls.xml"), "<HlsOptions />", CancellationToken);
        CreateDatabase();

        await CreateMigration().PerformAsync(CancellationToken);

        Assert.Null(_saved);
        using var context = CreateDbContext();
        Assert.False(context.Users.Include(u => u.Permissions).Single(u => u.Id.Equals(user)).HasPermission(PermissionKind.EnableOriginalPlayback));
        Assert.Equal(Path.Combine(_root, "hls"), Assert.Single(context.HlsRenditions).Base);
        Assert.Empty(context.NetworkStorageAccesses);
    }

    [Fact]
    public async Task Perform_S3StorageDisabled_ImportsIntoTheLocalDirectory()
    {
        await WritePluginAsync(Path.Combine(MappedDirectory, "hls"), Guid.NewGuid());
        await WriteS3StorageAsync(enabled: false);
        CreateDatabase();

        await CreateMigration().PerformAsync(CancellationToken);

        using var context = CreateDbContext();
        Assert.Equal(Path.Combine(MappedDirectory, "hls"), Assert.Single(context.HlsRenditions).Base);
        Assert.Empty(context.NetworkStorageAccesses);
    }

    [Fact]
    public async Task Perform_HlsDataExists_DoesNotImport()
    {
        await WritePluginAsync(Path.Combine(_root, "hls"), Guid.NewGuid());
        CreateDatabase();
        using (var context = CreateDbContext())
        {
            context.HlsVersionItems.Add(new HlsVersionItem { ItemId = Guid.NewGuid(), Quality = 0, VersionId = Guid.NewGuid(), Signature = string.Empty });
            await context.SaveChangesAsync(CancellationToken);
        }

        await CreateMigration().PerformAsync(CancellationToken);

        using (var context = CreateDbContext())
        {
            Assert.Empty(context.HlsMedia);
        }

        Assert.True(File.Exists(DatabasePath));
        Assert.NotNull(_saved);
    }

    [Fact]
    public async Task Perform_NoPlugin_DoesNothing()
    {
        await CreateMigration().PerformAsync(CancellationToken);

        Assert.Null(_saved);
        using var context = CreateDbContext();
        Assert.Empty(context.HlsMedia);
    }

    private async Task<Guid> AddUserAsync()
    {
        var user = new User("user", "default", "default");
        user.AddDefaultPermissions();
        using var context = CreateDbContext();
        context.Users.Add(user);
        await context.SaveChangesAsync(CancellationToken);
        return user.Id;
    }

    private Task WritePluginAsync(string outputPath, Guid originalUser)
        => File.WriteAllTextAsync(
            Path.Combine(PluginConfigurationsDirectory, "Jellyfin.Plugin.DistributedHls.xml"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <Enabled>true</Enabled>
              <AllowLocalOriginal>false</AllowLocalOriginal>
              <OriginalUsers><guid>{originalUser}</guid></OriginalUsers>
              <Libraries><guid>{_libraryId}</guid></Libraries>
              <Qualities><int>1080</int><int>720</int></Qualities>
              <OutputPath>{outputPath}</OutputPath>
              <LeaseMinutes>7</LeaseMinutes>
              <PlaybackTokenHours>24</PlaybackTokenHours>
              <VerifyModifiedTime>true</VerifyModifiedTime>
              <OrphanRetentionHours>72</OrphanRetentionHours>
              <MaxAttempts>3</MaxAttempts>
              <PlaybackSigningKey>{SigningKey}</PlaybackSigningKey>
            </PluginConfiguration>
            """,
            CancellationToken);

    // A bucket mapped to the parent of the output directory, and a longer mapping elsewhere that does not hold it.
    private Task WriteS3StorageAsync(bool enabled = true)
        => File.WriteAllTextAsync(
            Path.Combine(PluginConfigurationsDirectory, "Jellyfin.Plugin.S3Storage.xml"),
            $"""
            <?xml version="1.0" encoding="utf-8"?>
            <PluginConfiguration xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
              <Enabled>{(enabled ? "true" : "false")}</Enabled>
              <Buckets>
                <StorageBucket>
                  <Name>Other</Name>
                  <Endpoint>http://other.lan:9000</Endpoint>
                  <Bucket>other</Bucket>
                  <LocalPath>{Path.Combine(_root, "mnt-other", "deeper")}</LocalPath>
                </StorageBucket>
                <StorageBucket>
                  <Name>S3</Name>
                  <Endpoint>http://minio.lan:9000</Endpoint>
                  <Region>us-east-1</Region>
                  <Bucket>media-bucket</Bucket>
                  <KeyPrefix>/library/</KeyPrefix>
                  <LocalPath>{MappedDirectory}</LocalPath>
                  <AccessKey>key</AccessKey>
                  <SecretKey>secret</SecretKey>
                </StorageBucket>
              </Buckets>
            </PluginConfiguration>
            """,
            CancellationToken);

    private void CreateDatabase()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        var summary = JsonSerializer.Serialize(new HlsRenditionSummary(1080, 1920, 1080, 25, 5_000_000, 4_000_000, "avc1.640028"));
        var files = JsonSerializer.Serialize(new[] { new { Path = "init.mp4", Length = 900L, LastModified = DateTimeOffset.UnixEpoch } });
        command.CommandText = Schema + $"""
            INSERT INTO media VALUES('{Sha}','{MediaKey}','{_libraryId:N}',$probe,NULL,1760000000000);
            INSERT INTO sources VALUES('{_itemId:N}','{_libraryId:N}','/media/a.mkv',1000,{_modified.Ticks},'{Sha}');
            INSERT INTO sources VALUES('{Guid.NewGuid():N}','{_libraryId:N}','/media/b.mkv',1000,{_modified.Ticks},NULL);
            INSERT INTO renditions VALUES('{Sha}','v1080',2,NULL,'{_publishingJob:N}','{PublishedDirectory}',$summary,'h264-aac-sdr-v3',1760000000000);
            INSERT INTO renditions VALUES('{Sha}','v720',0,'{Guid.NewGuid():N}',NULL,NULL,NULL,NULL,NULL);
            INSERT INTO job_outputs VALUES('{_publishingJob:N}','v1080','{PublishedDirectory}',$summary,$files);
            INSERT INTO job_outputs VALUES('{Guid.NewGuid():N}','v720','{UnfinishedDirectory}',$summary,$files);
            INSERT INTO jobs VALUES('{Guid.NewGuid():N}','media:{Sha}',1,NULL,'{Sha}',$plan,1,'worker','44444444444444444444444444444444',{new DateTimeOffset(_leaseUntil).ToUnixTimeMilliseconds()},1,NULL,0,0);
            INSERT INTO jobs VALUES('{Guid.NewGuid():N}','probe:{_itemId:N}',0,'{_itemId:N}',NULL,$pending,0,NULL,NULL,NULL,0,NULL,0,0);
            INSERT INTO deletions VALUES('{QueuedDirectory}',1760000000000,2,'档位已取消',NULL);
            INSERT INTO version_items VALUES('{_itemId:N}',1080,'{_versionId:N}','signature');
            """;
        command.Parameters.AddWithValue("$probe", JsonSerializer.Serialize(_probe));
        command.Parameters.AddWithValue("$summary", summary);
        command.Parameters.AddWithValue("$files", files);
        command.Parameters.AddWithValue("$pending", "{\"Keys\":[\"a1\"]}");
        command.Parameters.AddWithValue("$plan", JsonSerializer.Serialize(new { Sha256 = Sha, Outputs = new[] { new { Key = "a1", RelativeDirectory = UploadingDirectory } } }));
        command.ExecuteNonQuery();
    }

    private MigrateDistributedHlsPlugin CreateMigration()
    {
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateDbContext);
        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.ConfigurationDirectoryPath).Returns(ConfigurationDirectory);
        paths.SetupGet(p => p.PluginConfigurationsPath).Returns(PluginConfigurationsDirectory);
        paths.SetupGet(p => p.DataPath).Returns(Path.Combine(_root, "data"));

        return new MigrateDistributedHlsPlugin(
            paths.Object,
            factory.Object,
            _configurationManager.Object,
            new StartupLogger<MigrateDistributedHlsPlugin>(NullLogger<MigrateDistributedHlsPlugin>.Instance),
            NullLogger<MigrateDistributedHlsPlugin>.Instance);
    }

    private JellyfinDbContext CreateDbContext()
        => new(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(Mock.Of<IApplicationPaths>(), NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
}
