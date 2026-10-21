using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Published;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Hls;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Server.Implementations.Tests.Hls.HlsTestEnvironment;

namespace Jellyfin.Server.Implementations.Tests.Hls;

public sealed class HlsTaskCenterTests : IDisposable
{
    // 60 seconds of 1080p with one audio track: two video renditions with the qualities of the environment, the audio and a preview.
    private const string ProbeJson = "{\"format\":{\"duration\":\"60\"},\"streams\":["
        + "{\"index\":0,\"codec_type\":\"video\",\"codec_name\":\"h264\",\"width\":1920,\"height\":1080,\"avg_frame_rate\":\"25/1\",\"pix_fmt\":\"yuv420p\"},"
        + "{\"index\":1,\"codec_type\":\"audio\",\"codec_name\":\"aac\",\"channels\":2,\"sample_rate\":48000}]}";

    private readonly List<IDisposable> _disposables = [];
    private readonly Mock<INetworkStorageManager> _networkStorage = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        foreach (var disposable in Enumerable.Reverse(_disposables))
        {
            disposable.Dispose();
        }
    }

    [Fact]
    public async Task Poll_ClaimsTheOldestJob_WithTheLeaseOfTheSettings()
    {
        var (environment, taskCenter) = Create();
        var item = await environment.AddSourceAsync();

        var job = await taskCenter.PollAsync("worker", CancellationToken);

        Assert.NotNull(job);
        Assert.Equal((300, 1), (job.Lease.LeaseSeconds, job.Lease.Attempt));
        Assert.Equal(LibraryFile(item), job.Source);
        Assert.Null(await taskCenter.PollAsync("worker", CancellationToken));
    }

    [Fact]
    public async Task Poll_WhileOff_ClaimsNothing_AndAWorkerNeedsAName()
    {
        var (environment, taskCenter) = Create();
        await environment.AddSourceAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => taskCenter.PollAsync(" ", CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => taskCenter.PollAsync(new string('w', 129), CancellationToken));
        environment.Save(new HlsOptions { Enabled = false, Store = environment.Root });
        Assert.Null(await taskCenter.PollAsync("worker", CancellationToken));
    }

    [Fact]
    public async Task GetInput_SendsALocalFile_OrRedirectsToTheTargetOfAStrmFile()
    {
        var (environment, taskCenter) = Create();
        _networkStorage.Setup(n => n.ResolveShortcutTarget("s3://bucket/a.mkv")).Returns("https://storage.example/bucket/a.mkv?X-Amz-Signature=0");
        var local = await environment.AddSourceAsync();
        var shortcut = Guid.NewGuid();
        await environment.Store.UpsertSourcesAsync([new HlsSourceFile(shortcut, LibraryId, "s3://bucket/a.mkv", 7000, Modified)], environment.Now, CancellationToken);
        await environment.Store.UpsertSourcesAsync([new HlsSourceFile(Guid.NewGuid(), LibraryId, "s3://other/b.mkv", 7000, Modified)], environment.Now, CancellationToken);

        var jobs = new Dictionary<string, HlsWorkLease>();
        while (await taskCenter.PollAsync("worker", CancellationToken) is { } job)
        {
            jobs[job.Source.Path] = job.Lease;
        }

        var file = jobs[LibraryFile(local).Path];
        Assert.Equal(new HlsJobInput(LibraryFile(local).Path, null), await taskCenter.GetInputAsync(file.JobId, file.LeaseToken, CancellationToken));
        var target = jobs["s3://bucket/a.mkv"];
        Assert.Equal(
            new HlsJobInput(null, new Uri("https://storage.example/bucket/a.mkv?X-Amz-Signature=0")),
            await taskCenter.GetInputAsync(target.JobId, target.LeaseToken, CancellationToken));
        Assert.Null(await taskCenter.GetInputAsync(target.JobId, "another lease", CancellationToken));

        // A bucket no access covers.
        var unreadable = jobs["s3://other/b.mkv"];
        await Assert.ThrowsAsync<IOException>(() => taskCenter.GetInputAsync(unreadable.JobId, unreadable.LeaseToken, CancellationToken));
    }

    [Fact]
    public async Task Identify_RecordsTheHash_AndReturnsThePlan()
    {
        var (environment, taskCenter) = Create();
        var item = await environment.AddSourceAsync();
        var lease = (await taskCenter.PollAsync("worker", CancellationToken))!.Lease;

        await Assert.ThrowsAsync<ArgumentException>(() => taskCenter.IdentifyAsync(lease.JobId, new HlsIdentification(lease.LeaseToken, "abc", ProbeJson), CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => taskCenter.IdentifyAsync(lease.JobId, new HlsIdentification(lease.LeaseToken, Sha('a'), "{}"), CancellationToken));
        Assert.Null(await taskCenter.IdentifyAsync(lease.JobId, new HlsIdentification("another lease", Sha('a'), ProbeJson), CancellationToken));

        var response = await taskCenter.IdentifyAsync(lease.JobId, new HlsIdentification(lease.LeaseToken, Sha('a').ToUpperInvariant(), ProbeJson), CancellationToken);

        Assert.NotNull(response);
        Assert.False(response.Done);
        Assert.Equal(["v1080", "v720", "a1", "preview"], response.Plan!.Renditions.Select(r => r.Key));
        Assert.Equal(Sha('a'), environment.Index.GetSource(item)!.Sha256);
    }

    [Fact]
    public async Task Identify_OfAContentWithNothingToEncode_IsDone()
    {
        var (environment, taskCenter) = Create();
        await environment.PublishAsync(Sha('a'));
        await environment.AddSourceAsync();
        var lease = (await taskCenter.PollAsync("worker", CancellationToken))!.Lease;

        var response = await taskCenter.IdentifyAsync(lease.JobId, new HlsIdentification(lease.LeaseToken, Sha('a'), ProbeJson), CancellationToken);

        Assert.Equal(new HlsIdentifyResponse(true, null), response);
    }

    [Fact]
    public async Task Uploads_OfALocalStore_GoThroughTheServer_AndOnlyForFilesOfThePlan()
    {
        var (environment, taskCenter) = Create();
        var (lease, _) = await IdentifiedAsync(environment, taskCenter);

        var targets = await taskCenter.GetUploadTargetsAsync(
            lease.JobId,
            new HlsUploadRequest(lease.LeaseToken, "a1", [new("init.mp4", 10), new(HlsRenditionPaths.GetSegmentFile(0), 20)]),
            CancellationToken);

        Assert.Equal(
            [new HlsUploadTarget("init.mp4", null, "audio/mp4"), new HlsUploadTarget(HlsRenditionPaths.GetSegmentFile(0), null, "audio/mp4")],
            targets);
        Assert.Null(await taskCenter.GetUploadTargetsAsync(lease.JobId, new HlsUploadRequest("another lease", "a1", [new("init.mp4", 10)]), CancellationToken));
        foreach (var refused in new[]
        {
            new HlsUploadRequest(lease.LeaseToken, "v2160", [new("init.mp4", 10)]),
            new HlsUploadRequest(lease.LeaseToken, "a1", [new("../init.mp4", 10)]),
            new HlsUploadRequest(lease.LeaseToken, "a1", [new(HlsRenditionPaths.GetSegmentFile(12), 10)]),
            new HlsUploadRequest(lease.LeaseToken, "preview", [new("init.mp4", 10)]),
            new HlsUploadRequest(lease.LeaseToken, "a1", []),
            new HlsUploadRequest(lease.LeaseToken, "a1", [null!]),
            new HlsUploadRequest(lease.LeaseToken, "a1", Enumerable.Range(0, 501).Select(i => new HlsOutputFile("init.mp4", 10)).ToList())
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => taskCenter.GetUploadTargetsAsync(lease.JobId, refused, CancellationToken));
        }
    }

    [Fact]
    public async Task Uploads_BeforeTheFileIsIdentified_AreRefused()
    {
        var (environment, taskCenter) = Create();
        await environment.AddSourceAsync();
        var lease = (await taskCenter.PollAsync("worker", CancellationToken))!.Lease;

        await Assert.ThrowsAsync<ArgumentException>(
            () => taskCenter.GetUploadTargetsAsync(lease.JobId, new HlsUploadRequest(lease.LeaseToken, "a1", [new("init.mp4", 10)]), CancellationToken));
    }

    [Fact]
    public async Task Uploads_OfNetworkStorage_AreSigned()
    {
        var store = new Mock<IHlsStore>();
        store.Setup(s => s.GetUploadAddress(It.IsAny<string>(), TimeSpan.FromHours(1)))
            .Returns((string path, TimeSpan _) => new Uri("https://storage.example/hls/" + path + "?X-Amz-Signature=0"));
        var (environment, taskCenter) = Create(store.Object);
        var (lease, plan) = await IdentifiedAsync(environment, taskCenter);

        var target = Assert.Single((await taskCenter.GetUploadTargetsAsync(lease.JobId, new HlsUploadRequest(lease.LeaseToken, "preview", [new("preview.jpg", 10)]), CancellationToken))!);

        var directory = plan.Renditions.Single(r => r.Key == "preview").RelativeDirectory;
        Assert.Equal(new HlsUploadTarget("preview.jpg", new Uri("https://storage.example/hls/" + directory + "/preview.jpg?X-Amz-Signature=0"), "image/jpeg"), target);
        await Assert.ThrowsAsync<ArgumentException>(() => taskCenter.WriteFileAsync(lease.JobId, lease.LeaseToken, "preview", "preview.jpg", Stream.Null, CancellationToken));
    }

    [Fact]
    public async Task WriteFile_WritesIntoTheDirectoryOfTheRendition()
    {
        var (environment, taskCenter) = Create();
        var (lease, plan) = await IdentifiedAsync(environment, taskCenter);
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("init"));

        Assert.True(await taskCenter.WriteFileAsync(lease.JobId, lease.LeaseToken, "a1", "init.mp4", content, CancellationToken));
        Assert.False(await taskCenter.WriteFileAsync(lease.JobId, "another lease", "a1", "init.mp4", Stream.Null, CancellationToken));

        var directory = plan.Renditions.Single(r => r.Key == "a1").RelativeDirectory;
        Assert.Equal("init", await File.ReadAllTextAsync(Path.Combine(environment.GetStorePath(directory), "init.mp4"), CancellationToken));
    }

    [Fact]
    public async Task Uploads_IntoARootThatCannotBeTrusted_AreRefused()
    {
        var (environment, taskCenter) = Create();
        await environment.PublishAsync(Sha('b'));
        var (lease, _) = await IdentifiedAsync(environment, taskCenter);
        var request = new HlsUploadRequest(lease.LeaseToken, "a1", [new("init.mp4", 1)]);
        await taskCenter.GetUploadTargetsAsync(lease.JobId, request, CancellationToken);

        // The mount is lost: what is left is the empty mount point, which has neither the marker nor what was published.
        Directory.Delete(environment.Root, true);
        Directory.CreateDirectory(environment.Root);
        using var content = new MemoryStream([1]);

        await Assert.ThrowsAsync<IOException>(() => taskCenter.WriteFileAsync(lease.JobId, lease.LeaseToken, "a1", "init.mp4", content, CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => taskCenter.GetUploadTargetsAsync(lease.JobId, request, CancellationToken));
        await Assert.ThrowsAsync<IOException>(() => taskCenter.StartUploadCheckAsync("worker", CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(environment.Root));
        Assert.DoesNotContain(await environment.DeletionsAsync(), d => d.RelativeDirectory.StartsWith(".jellyfin-hls-test-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Complete_RecordsTheOutputs_ForTheVerification()
    {
        var (environment, taskCenter) = Create();
        var (lease, plan) = await IdentifiedAsync(environment, taskCenter);

        await Assert.ThrowsAsync<ArgumentException>(() => taskCenter.CompleteAsync(lease.JobId, new HlsCompletion(lease.LeaseToken, []), CancellationToken));
        Assert.False(await taskCenter.CompleteAsync(lease.JobId, new HlsCompletion("another lease", Outputs(plan)), CancellationToken));
        Assert.True(await taskCenter.CompleteAsync(lease.JobId, new HlsCompletion(lease.LeaseToken, Outputs(plan)), CancellationToken));

        Assert.Equal((int)HlsJobStatus.Verifying, (await environment.JobAsync(lease.JobId)).Status);
    }

    [Fact]
    public async Task Fail_KeepsTheFirst2048CharactersOfTheError()
    {
        var (environment, taskCenter) = Create();
        await environment.AddSourceAsync();
        var lease = (await taskCenter.PollAsync("worker", CancellationToken))!.Lease;

        Assert.False(await taskCenter.FailAsync(lease.JobId, new HlsFailure("another lease", "ffmpeg failed"), CancellationToken));
        Assert.True(await taskCenter.FailAsync(lease.JobId, new HlsFailure(lease.LeaseToken, new string('e', 3000)), CancellationToken));

        var job = await environment.JobAsync(lease.JobId);
        Assert.Equal((int)HlsJobStatus.Pending, job.Status);
        Assert.Equal(new string('e', 2048), job.Error);
    }

    [Fact]
    public async Task Fail_KeepsTheDirectories_UntilTheUploadAddressesExpired()
    {
        var (environment, taskCenter) = Create();
        var (lease, plan) = await IdentifiedAsync(environment, taskCenter);
        var before = DateTime.UtcNow;

        Assert.True(await taskCenter.FailAsync(lease.JobId, new HlsFailure(lease.LeaseToken, "ffmpeg failed"), CancellationToken));

        var deletions = await environment.DeletionsAsync();
        Assert.Equal(plan.Renditions.Select(r => r.RelativeDirectory).Order(StringComparer.Ordinal), deletions.Select(d => d.RelativeDirectory).Order(StringComparer.Ordinal));
        Assert.All(deletions, d => Assert.True(d.NotBefore >= before + TimeSpan.FromHours(1) + Lease));
    }

    [Fact]
    public async Task UploadCheck_OfALocalStore_IsWrittenAndDeleted_Once()
    {
        var (environment, taskCenter) = Create();

        var check = await taskCenter.StartUploadCheckAsync("worker", CancellationToken);

        Assert.Null(check.Address);
        var directory = ".jellyfin-hls-test-" + check.Id.ToString("N");
        Assert.Equal(directory + "/check", check.Path);
        Assert.Equal([directory], (await environment.DeletionsAsync()).Select(d => d.RelativeDirectory));
        using var content = new MemoryStream([1, 2, 3]);

        // The store is gone for a moment; the check is not used up.
        Directory.Delete(environment.Root);
        await Assert.ThrowsAsync<IOException>(() => taskCenter.WriteUploadCheckAsync(check.Id, content, CancellationToken));
        Directory.CreateDirectory(environment.Root);

        Assert.True(await taskCenter.WriteUploadCheckAsync(check.Id, content, CancellationToken));
        Assert.False(Directory.Exists(environment.GetStorePath(directory)));
        Assert.False(await taskCenter.WriteUploadCheckAsync(check.Id, Stream.Null, CancellationToken));
        Assert.False(await taskCenter.WriteUploadCheckAsync(Guid.NewGuid(), Stream.Null, CancellationToken));
    }

    [Fact]
    public async Task UploadCheck_OfNetworkStorage_IsSigned_AndQueuedForDeletion()
    {
        var store = new Mock<IHlsStore>();
        store.Setup(s => s.GetUploadAddress(It.IsAny<string>(), TimeSpan.FromHours(1)))
            .Returns((string path, TimeSpan _) => new Uri("https://storage.example/hls/" + path));
        var (environment, taskCenter) = Create(store.Object);

        var check = await taskCenter.StartUploadCheckAsync("worker", CancellationToken);

        Assert.Equal(new Uri("https://storage.example/hls/" + check.Path), check.Address);
        Assert.Equal(check.Path[..^"/check".Length], Assert.Single(await environment.DeletionsAsync()).RelativeDirectory);
        Assert.False(await taskCenter.WriteUploadCheckAsync(check.Id, Stream.Null, CancellationToken));
    }

    [Fact]
    public async Task Status_HoldsTheSettings_TheCounts_AndTheStore()
    {
        var (environment, taskCenter) = Create();
        await environment.AddSourceAsync();

        var status = await taskCenter.GetStatusAsync(CancellationToken);

        Assert.True(status.Enabled);
        Assert.Null(status.SettingsError);
        Assert.Equal(1, status.Counts.Pending);
        Assert.Single(await taskCenter.GetJobsAsync(0, CancellationToken));
    }

    // A claimed job whose file is identified as content a.
    private static async Task<(HlsWorkLease Lease, HlsEncodePlan Plan)> IdentifiedAsync(HlsTestEnvironment environment, HlsTaskCenter taskCenter)
    {
        await environment.AddSourceAsync();
        var lease = (await taskCenter.PollAsync("worker", CancellationToken))!.Lease;
        var response = await taskCenter.IdentifyAsync(lease.JobId, new HlsIdentification(lease.LeaseToken, Sha('a'), ProbeJson), CancellationToken);
        return (lease, response!.Plan!);
    }

    // A new local store in a temporary directory, or the store given.
    private (HlsTestEnvironment Environment, HlsTaskCenter TaskCenter) Create(IHlsStore? store = null)
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
        Directory.CreateDirectory(environment.Root);
        var coordinator = new HlsCoordinator(
            environment.Store,
            environment.Settings,
            environment.Index,
            Mock.Of<ILibraryManager>(),
            _networkStorage.Object,
            Mock.Of<ITrueCacheService>(),
            NullLogger<HlsCoordinator>.Instance);
        _disposables.Add(coordinator);
        var verification = new HlsVerificationService(
            environment.Store,
            environment.Settings,
            environment.Monitor,
            environment.Index,
            coordinator,
            NullLogger<HlsVerificationService>.Instance);
        _disposables.Add(verification);
        var taskCenter = new HlsTaskCenter(
            environment.Store,
            environment.Settings,
            environment.Index,
            coordinator,
            verification,
            environment.Monitor,
            _networkStorage.Object,
            NullLogger<HlsTaskCenter>.Instance);
        return (environment, taskCenter);
    }
}
