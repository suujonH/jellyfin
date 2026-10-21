using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Controller.Hls;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Server.Implementations.Tests.Hls.HlsTestEnvironment;

namespace Jellyfin.Server.Implementations.Tests.Hls;

public sealed class HlsDeletionServiceTests : IDisposable
{
    private const string Due = "0123456789abcdef0123456789abcdef/video/1080/00000000000000000000000000000001";
    private const string Later = "0123456789abcdef0123456789abcdef/video/720/00000000000000000000000000000002";

    private readonly Mock<IHlsStore> _store = new();
    private HlsTestEnvironment? _environment;
    private HlsDeletionService? _service;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        _service?.Dispose();
        _environment?.Dispose();
    }

    [Fact]
    public async Task DueDirectories_AreDeleted_AndLeaveTheQueue()
    {
        var (environment, service) = Create(local: true);
        Directory.CreateDirectory(environment.Root);
        await File.WriteAllTextAsync(Path.Combine(environment.Root, ".jellyfin-hls"), "marked", CancellationToken);
        foreach (var directory in new[] { Due, Later })
        {
            Directory.CreateDirectory(environment.GetStorePath(directory));
            await File.WriteAllTextAsync(Path.Combine(environment.GetStorePath(directory), "init.mp4"), "init", CancellationToken);
        }

        await QueueAsync(environment, Due, environment.Now - TimeSpan.FromMinutes(1));
        await QueueAsync(environment, Later, environment.Now + TimeSpan.FromHours(1));

        await service.ProcessAsync(environment.Now, CancellationToken);

        Assert.False(Directory.Exists(environment.GetStorePath(Due)));
        Assert.True(Directory.Exists(environment.GetStorePath(Later)));
        Assert.Equal([Later], (await environment.DeletionsAsync()).Select(d => d.RelativeDirectory));
    }

    [Fact]
    public async Task AFailure_PostponesTheDirectory_FromOneMinuteUpToSixHours()
    {
        const string Ninth = "0123456789abcdef0123456789abcdef/audio/1/00000000000000000000000000000003";
        _store.Setup(s => s.CheckRootAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        _store.Setup(s => s.DeleteDirectoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ThrowsAsync(new IOException("Access denied"));
        var (environment, service) = Create(local: false);
        var now = environment.Now;
        await QueueAsync(environment, Due, now);
        await QueueAsync(environment, Later, now, attempts: 3);
        await QueueAsync(environment, Ninth, now, attempts: 9);

        await service.ProcessAsync(now, CancellationToken);

        var deletions = (await environment.DeletionsAsync()).ToDictionary(d => d.RelativeDirectory);
        Assert.Equal((1, now + TimeSpan.FromMinutes(1), "Access denied"), (deletions[Due].Attempts, deletions[Due].NotBefore, deletions[Due].LastError));
        Assert.Equal((4, now + TimeSpan.FromMinutes(8)), (deletions[Later].Attempts, deletions[Later].NotBefore));
        Assert.Equal((10, now + TimeSpan.FromHours(6)), (deletions[Ninth].Attempts, deletions[Ninth].NotBefore));
    }

    [Fact]
    public async Task AnUntrustedRoot_TouchesNothing()
    {
        _store.Setup(s => s.CheckRootAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync("The HLS store cannot be used: gone");
        var (environment, service) = Create(local: false);
        await QueueAsync(environment, Due, environment.Now);

        await service.ProcessAsync(environment.Now, CancellationToken);

        _store.Verify(s => s.DeleteDirectoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(0, Assert.Single(await environment.DeletionsAsync()).Attempts);
    }

    [Fact]
    public async Task ARootLostDuringTheDeletion_KeepsTheDirectoryQueued()
    {
        // A directory deleted from a root that is gone afterwards may only have seemed to be there.
        _store.SetupSequence(s => s.CheckRootAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null)
            .ReturnsAsync("The HLS store cannot be used: gone");
        var (environment, service) = Create(local: false);
        await QueueAsync(environment, Due, environment.Now);

        await service.ProcessAsync(environment.Now, CancellationToken);

        _store.Verify(s => s.DeleteDirectoryAsync(Due, It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(0, Assert.Single(await environment.DeletionsAsync()).Attempts);
    }

    [Fact]
    public async Task AnInvalidDirectory_IsDroppedWithoutDeletingAnything()
    {
        _store.Setup(s => s.CheckRootAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>())).ReturnsAsync((string?)null);
        var (environment, service) = Create(local: false);
        await QueueAsync(environment, "../outside", environment.Now);

        await service.ProcessAsync(environment.Now, CancellationToken);

        _store.Verify(s => s.DeleteDirectoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Empty(await environment.DeletionsAsync());
    }

    [Fact]
    public async Task TheDirectoryOfAnUploadCheck_IsDeletedToo()
    {
        var (environment, service) = Create(local: true);
        Directory.CreateDirectory(environment.Root);
        await File.WriteAllTextAsync(Path.Combine(environment.Root, ".jellyfin-hls"), "marked", CancellationToken);
        var directory = ".jellyfin-hls-test-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(environment.GetStorePath(directory));
        await File.WriteAllTextAsync(Path.Combine(environment.GetStorePath(directory), "check"), "check", CancellationToken);
        await environment.Store.QueueUploadCheckDeletionAsync(directory, environment.Now, CancellationToken);
        await Assert.ThrowsAsync<ArgumentException>(() => environment.Store.QueueUploadCheckDeletionAsync(".jellyfin-hls-test-check", environment.Now, CancellationToken));

        await service.ProcessAsync(environment.Now, CancellationToken);

        Assert.False(Directory.Exists(environment.GetStorePath(directory)));
        Assert.Empty(await environment.DeletionsAsync());
    }

    [Fact]
    public async Task NothingDue_LeavesTheStoreAlone()
    {
        var (environment, service) = Create(local: false);
        await QueueAsync(environment, Due, environment.Now + TimeSpan.FromMinutes(1));

        await service.ProcessAsync(environment.Now, CancellationToken);

        // Checking a network store costs requests, so it is not checked every ten seconds for nothing.
        _store.Verify(s => s.CheckRootAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // Rows written as the job store queues them; only the path checks of the service stand between them and the store.
    private static async Task QueueAsync(HlsTestEnvironment environment, string directory, DateTime notBefore, int attempts = 0)
    {
        await using var context = environment.CreateDbContext();
        context.HlsDeletions.Add(new HlsDeletion { RelativeDirectory = directory, NotBefore = notBefore, Attempts = attempts, Reason = "Test" });
        await context.SaveChangesAsync(CancellationToken);
    }

    private (HlsTestEnvironment Environment, HlsDeletionService Service) Create(bool local)
    {
        IHlsStoreFactory? factory = null;
        if (!local)
        {
            var mock = new Mock<IHlsStoreFactory>();
            mock.Setup(f => f.Create(It.IsAny<string>())).Returns(_store.Object);
            factory = mock.Object;
        }

        _environment = new HlsTestEnvironment(factory);
        _service = new HlsDeletionService(_environment.Store, _environment.Settings, _environment.Monitor, NullLogger<HlsDeletionService>.Instance);
        return (_environment, _service);
    }
}
