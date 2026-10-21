using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Published;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Hls;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Server.Implementations.Tests.Hls.HlsTestEnvironment;

namespace Jellyfin.Server.Implementations.Tests.Hls;

public sealed class HlsVerificationServiceTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        // Services before the environment whose store they use.
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task VerifiedOutputs_ArePublished_AndReachTheIndex()
    {
        var (environment, service) = Create();
        var (item, lease, _) = await environment.SubmitAsync(Sha('a'));
        await environment.Index.LoadAsync(CancellationToken);
        Assert.Null(environment.Index.GetMediaForItem(item));

        await service.VerifyBatchAsync(environment.Now, CancellationToken);

        Assert.Equal((int)HlsJobStatus.Complete, (await environment.JobAsync(lease.Id)).Status);
        Assert.All(await environment.Store.GetRenditionsAsync(Sha('a'), CancellationToken), r => Assert.Equal(HlsRenditionState.Published, r.State));
        Assert.True(environment.Index.GetMediaForItem(item)!.Playable);
        Assert.Empty(await environment.DeletionsAsync());
    }

    [Fact]
    public async Task AFileMissing_FailsTheVerification_AndQueuesTheOutputs()
    {
        var (environment, service) = Create();
        var (_, lease, plan) = await environment.SubmitAsync(Sha('a'));
        var v720 = plan.Renditions.Single(r => r.Key == "v720").RelativeDirectory;
        File.Delete(Path.Combine(environment.GetStorePath(v720), HlsRenditionPaths.GetSegmentFile(3)));

        await service.VerifyBatchAsync(environment.Now, CancellationToken);

        // Tried again: the first attempt of three.
        var job = await environment.JobAsync(lease.Id);
        Assert.Equal((int)HlsJobStatus.Pending, job.Status);
        Assert.Equal("Verification failed: v720: The file segment-000003.m4s is missing.", job.Error);
        Assert.Equal(plan.Renditions.Select(r => r.RelativeDirectory).Order(StringComparer.Ordinal), (await environment.DeletionsAsync()).Select(d => d.RelativeDirectory));
        Assert.All(await environment.Store.GetRenditionsAsync(Sha('a'), CancellationToken), r => Assert.Equal(HlsRenditionState.Missing, r.State));
    }

    [Fact]
    public async Task AnUntrustedRoot_LeavesTheJobVerifying()
    {
        var (environment, service) = Create();
        var (_, lease, _) = await environment.SubmitAsync(Sha('a'));

        // What a mount point looks like without its mount: the files seem to be gone.
        Directory.Delete(environment.Root, true);
        await service.VerifyBatchAsync(environment.Now, CancellationToken);

        Assert.Equal((int)HlsJobStatus.Verifying, (await environment.JobAsync(lease.Id)).Status);
        Assert.Empty(await environment.DeletionsAsync());
        Assert.NotNull(environment.Monitor.Problem);
    }

    [Fact]
    public async Task ARootLostDuringTheVerification_LeavesTheJobVerifying()
    {
        var store = new Mock<IHlsStore>();
        store.SetupSequence(s => s.CheckRootAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null)
            .ReturnsAsync("The HLS store cannot be used: gone");
        store.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var (environment, service) = Create(store.Object);
        var (_, lease, _) = await environment.SubmitAsync(Sha('a'));

        await service.VerifyBatchAsync(environment.Now, CancellationToken);

        Assert.Equal((int)HlsJobStatus.Verifying, (await environment.JobAsync(lease.Id)).Status);
        Assert.Empty(await environment.DeletionsAsync());
    }

    [Fact]
    public async Task Reverify_RetiresARenditionWhoseFilesAreGone()
    {
        var (environment, service) = Create();
        var (item, _, plan) = await environment.SubmitAsync(Sha('a'));
        await environment.Index.LoadAsync(CancellationToken);
        await service.VerifyBatchAsync(environment.Now, CancellationToken);
        var v720 = plan.Renditions.Single(r => r.Key == "v720").RelativeDirectory;
        Directory.Delete(environment.GetStorePath(v720), true);

        await service.ReverifyAsync(null, CancellationToken);

        var report = service.LastReverify!;
        Assert.Equal((5, 1, null), (report.Checked, report.Failed, report.Aborted));
        var rendition = (await environment.Store.GetRenditionsAsync(Sha('a'), CancellationToken)).Single(r => r.Key == "v720");
        Assert.Equal(HlsRenditionState.Missing, rendition.State);
        Assert.Equal([v720], (await environment.DeletionsAsync()).Select(d => d.RelativeDirectory));
        Assert.Equal(["v1080"], environment.Index.GetMediaForItem(item)!.Video.Select(r => r.Key));
    }

    [Fact]
    public async Task Reverify_ForAnItem_ChecksItsContentOnly()
    {
        var (environment, service) = Create();
        var (item, _, _) = await environment.SubmitAsync(Sha('a'));
        await environment.SubmitAsync(Sha('b'));
        await environment.Index.LoadAsync(CancellationToken);
        await service.VerifyBatchAsync(environment.Now, CancellationToken);

        await service.ReverifyAsync(item, CancellationToken);
        Assert.Equal((5, 0, null), (service.LastReverify!.Checked, service.LastReverify.Failed, service.LastReverify.Aborted));

        await service.ReverifyAsync(null, CancellationToken);
        Assert.Equal(10, service.LastReverify!.Checked);

        await service.ReverifyAsync(Guid.NewGuid(), CancellationToken);
        Assert.Equal(0, service.LastReverify!.Checked);
    }

    [Fact]
    public async Task Reverify_StopsWhileTheRootCannotBeTrusted()
    {
        var (environment, service) = Create();
        await environment.SubmitAsync(Sha('a'));
        await service.VerifyBatchAsync(environment.Now, CancellationToken);

        Directory.Delete(environment.Root, true);
        await service.ReverifyAsync(null, CancellationToken);

        Assert.Equal((0, 0), (service.LastReverify!.Checked, service.LastReverify.Failed));
        Assert.NotNull(service.LastReverify.Aborted);
        Assert.All(await environment.Store.GetRenditionsAsync(Sha('a'), CancellationToken), r => Assert.Equal(HlsRenditionState.Published, r.State));
        Assert.Empty(await environment.DeletionsAsync());
    }

    [Fact]
    public async Task Reverify_AsksForTheRootOnEveryFailure()
    {
        // Every rendition seems gone, and the root with them after the first.
        var store = new Mock<IHlsStore>();
        store.SetupSequence(s => s.CheckRootAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null)
            .ReturnsAsync((string?)null)
            .ReturnsAsync("The HLS store cannot be used: gone");
        store.Setup(s => s.ListAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var (environment, service) = Create(store.Object);
        var (item, _) = await environment.PublishAsync(Sha('a'));
        await environment.Index.LoadAsync(CancellationToken);

        await service.ReverifyAsync(null, CancellationToken);

        Assert.Equal((2, 1), (service.LastReverify!.Checked, service.LastReverify.Failed));
        Assert.NotNull(service.LastReverify.Aborted);
        Assert.Single(await environment.DeletionsAsync());

        // The manifests come by key, so the audio was retired; it leaves playback at once.
        var renditions = await environment.Store.GetRenditionsAsync(Sha('a'), CancellationToken);
        Assert.Equal(["a1"], renditions.Where(r => r.State != HlsRenditionState.Published).Select(r => r.Key));
        Assert.Empty(environment.Index.GetMediaForItem(item)!.Audio);
    }

    // A local store in a temporary directory, or the store given.
    private (HlsTestEnvironment Environment, HlsVerificationService Service) Create(IHlsStore? store = null)
    {
        IHlsStoreFactory? factory = null;
        if (store is not null)
        {
            var mock = new Mock<IHlsStoreFactory>();
            mock.Setup(f => f.Create(It.IsAny<string>())).Returns(store);
            factory = mock.Object;
        }

        var environment = new HlsTestEnvironment(factory);
        _disposables.Add(environment);
        var coordinator = new HlsCoordinator(
            environment.Store,
            environment.Settings,
            environment.Index,
            Mock.Of<ILibraryManager>(),
            Mock.Of<INetworkStorageManager>(),
            Mock.Of<ITrueCacheService>(),
            NullLogger<HlsCoordinator>.Instance);
        _disposables.Add(coordinator);
        var service = new HlsVerificationService(
            environment.Store,
            environment.Settings,
            environment.Monitor,
            environment.Index,
            coordinator,
            NullLogger<HlsVerificationService>.Instance);
        _disposables.Add(service);
        return (environment, service);
    }
}
