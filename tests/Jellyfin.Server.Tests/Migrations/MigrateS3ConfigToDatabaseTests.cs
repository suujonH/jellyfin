using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.Serialization;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Locking;
using Jellyfin.Database.Providers.Sqlite;
using Jellyfin.Server.Migrations.Routines;
using Jellyfin.Server.ServerSetupApp;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities.TV;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Tests.Migrations;

public sealed class MigrateS3ConfigToDatabaseTests : IDisposable
{
    private const string Config = """
        <?xml version="1.0" encoding="utf-8"?>
        <S3Options xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema">
          <Buckets>
            <S3BucketOptions>
              <Name>media</Name>
              <Endpoint>http://minio.lan:9000</Endpoint>
              <Region>us-east-1</Region>
              <UsePathStyle>true</UsePathStyle>
              <AccessKey>key</AccessKey>
              <SecretKey>secret</SecretKey>
            </S3BucketOptions>
            <S3BucketOptions>
              <Name></Name>
              <Endpoint>https://account.r2.cloudflarestorage.com</Endpoint>
              <Region>auto</Region>
              <AccessKey>fallback</AccessKey>
              <SecretKey>fallback-secret</SecretKey>
            </S3BucketOptions>
          </Buckets>
          <UrlLifetimeSeconds>3600</UrlLifetimeSeconds>
        </S3Options>
        """;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<JellyfinDbContext> _dbOptions;
    private readonly string _configDirectory = Path.Combine(Path.GetTempPath(), "jellyfin-s3-migration-" + Guid.NewGuid().ToString("N"));

    public MigrateS3ConfigToDatabaseTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<JellyfinDbContext>().UseSqlite(_connection).Options;
        using var context = CreateDbContext();
        context.Database.EnsureCreated();
        Directory.CreateDirectory(_configDirectory);
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_configDirectory, true);
    }

    [Fact]
    public async Task Perform_S3Config_BecomesOneAccessPerBucket()
    {
        await File.WriteAllTextAsync(Path.Combine(_configDirectory, "s3.xml"), Config, TestContext.Current.CancellationToken);
        using (var context = CreateDbContext())
        {
            // The unnamed entry covered the buckets the library names that no other entry names.
            context.BaseItems.Add(Item("s3://media/a.mkv"));
            context.BaseItems.Add(Item("S3://archive/b.mkv"));
            context.BaseItems.Add(Item("https://cdn.example.com/c.mkv"));
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await CreateMigration().PerformAsync(TestContext.Current.CancellationToken);

        using (var context = CreateDbContext())
        {
            var accesses = context.NetworkStorageAccesses.OrderBy(a => a.Bucket).ToList();
            Assert.Equal(["archive", "media"], accesses.Select(a => a.Bucket));
            Assert.Equal("fallback", accesses[0].AccessKey);
            Assert.Equal("auto", accesses[0].Region);
            Assert.Equal("secret", accesses[1].SecretKey);
            Assert.True(accesses[1].UsePathStyle);
            Assert.All(accesses, a => Assert.Equal(3600, a.UrlLifetimeSeconds));
            Assert.All(accesses, a => Assert.True(a.IsArchive));
        }

        Assert.False(File.Exists(Path.Combine(_configDirectory, "s3.xml")));
        Assert.True(File.Exists(Path.Combine(_configDirectory, "s3.xml.migrated")));
    }

    [Fact]
    public async Task Perform_NoS3Config_DoesNothing()
    {
        await CreateMigration().PerformAsync(TestContext.Current.CancellationToken);

        using var context = CreateDbContext();
        Assert.Empty(context.NetworkStorageAccesses);
    }

    private static BaseItemEntity Item(string target) => new()
    {
        Id = Guid.NewGuid(),
        Type = typeof(Episode).FullName!,
        Path = "/tv/" + Guid.NewGuid().ToString("N") + ".strm",
        Data = JsonSerializer.Serialize(new { ShortcutPath = target })
    };

    private MigrateS3ConfigToDatabase CreateMigration()
    {
        var factory = new Mock<IDbContextFactory<JellyfinDbContext>>();
        factory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(CreateDbContext);
        var paths = new Mock<IServerApplicationPaths>();
        paths.SetupGet(p => p.ConfigurationDirectoryPath).Returns(_configDirectory);

        return new MigrateS3ConfigToDatabase(
            paths.Object,
            factory.Object,
            new MyXmlSerializer(),
            new StartupLogger<MigrateS3ConfigToDatabase>(NullLogger<MigrateS3ConfigToDatabase>.Instance),
            NullLogger<MigrateS3ConfigToDatabase>.Instance);
    }

    private JellyfinDbContext CreateDbContext()
        => new(
            _dbOptions,
            NullLogger<JellyfinDbContext>.Instance,
            new SqliteDatabaseProvider(Mock.Of<IApplicationPaths>(), NullLogger<SqliteDatabaseProvider>.Instance),
            new NoLockBehavior(NullLogger<NoLockBehavior>.Instance));
}
