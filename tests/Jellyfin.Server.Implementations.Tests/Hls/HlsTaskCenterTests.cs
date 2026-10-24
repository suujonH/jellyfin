using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Published;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
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
    private string _library = string.Empty;
    private string _data = string.Empty;

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

        var job = await taskCenter.PollAsync(new HlsWorkerRequest("worker", WorkerId), CancellationToken);

        Assert.NotNull(job);
        Assert.Equal((300, 1), (job.Lease.LeaseSeconds, job.Lease.Attempt));
        Assert.Equal(LibraryFile(item), job.Source);
        Assert.Null(await taskCenter.PollAsync(new HlsWorkerRequest("worker", WorkerId), CancellationToken));
    }

    [Fact]
    public async Task Poll_WhileOff_ClaimsNothing_AndAWorkerNeedsAName()
    {
        var (environment, taskCenter) = Create();
        await environment.AddSourceAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => taskCenter.PollAsync(new HlsWorkerRequest(" ", WorkerId), CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => taskCenter.PollAsync(new HlsWorkerRequest(new string('w', 129), WorkerId), CancellationToken));
        environment.Save(new HlsOptions { Enabled = false });
        Assert.Null(await taskCenter.PollAsync(new HlsWorkerRequest("worker", WorkerId), CancellationToken));
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
        while (await taskCenter.PollAsync(new HlsWorkerRequest("worker", WorkerId), CancellationToken) is { } job)
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
    public async Task GetInput_OfAFileOnTheWorker_IsReadByTheWorker()
    {
        var (environment, taskCenter) = Create();
        var target = HlsWorkerLocalAddress.Format(WorkerId, "/srv/movies/a.mkv");
        await environment.Store.UpsertSourcesAsync([new HlsSourceFile(Guid.NewGuid(), LibraryId, target, 7000, Modified)], environment.Now, CancellationToken);

        Assert.Null(await taskCenter.PollAsync(new HlsWorkerRequest("worker", OtherWorkerId), CancellationToken));
        var job = (await taskCenter.PollAsync(new HlsWorkerRequest("worker", WorkerId), CancellationToken))!;

        Assert.Equal(target, job.Source.Path);
        Assert.Equal(new HlsJobInput(null, null), await taskCenter.GetInputAsync(job.Lease.JobId, job.Lease.LeaseToken, CancellationToken));
        Assert.True(await taskCenter.ReturnAsync(job.Lease.JobId, job.Lease.LeaseToken, CancellationToken));
        Assert.Equal(job.Lease.JobId, (await taskCenter.PollAsync(new HlsWorkerRequest("worker", WorkerId), CancellationToken))!.Lease.JobId);
    }

    [Fact]
    public async Task Poll_FollowsTheLibrariesAndExclusionsOfTheWorker_WhichNeedsAnId()
    {
        var (environment, taskCenter) = Create();
        var excluded = await environment.AddSourceAsync();
        var item = await environment.AddSourceAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => taskCenter.PollAsync(new HlsWorkerRequest("worker", Guid.Empty), CancellationToken));
        Assert.Null(await taskCenter.PollAsync(new HlsWorkerRequest("worker", WorkerId, [OtherLibraryId]), CancellationToken));
        var job = await taskCenter.PollAsync(new HlsWorkerRequest("worker", WorkerId, [LibraryId], [excluded]), CancellationToken);
        Assert.Equal(item, job!.Source.ItemId);
    }

    [Fact]
    public async Task RegisterWorker_NeedsAName()
    {
        var (_, taskCenter) = Create();

        Assert.Equal(WorkerId, await taskCenter.RegisterWorkerAsync(new HlsWorkerRegistration(WorkerId, "worker"), CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => taskCenter.RegisterWorkerAsync(new HlsWorkerRegistration(null, " "), CancellationToken));
    }

    [Fact]
    public async Task Identify_RecordsTheHash_AndReturnsThePlan()
    {
        var (environment, taskCenter) = Create();
        var item = await environment.AddSourceAsync();
        var lease = (await taskCenter.PollAsync(new HlsWorkerRequest("worker", WorkerId), CancellationToken))!.Lease;

        await Assert.ThrowsAsync<ArgumentException>(() => taskCenter.IdentifyAsync(lease.JobId, new HlsIdentification(lease.LeaseToken, "abc", ProbeJson, environment.Root), CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => taskCenter.IdentifyAsync(lease.JobId, new HlsIdentification(lease.LeaseToken, Sha('a'), "{}", environment.Root), CancellationToken));
        Assert.Null(await taskCenter.IdentifyAsync(lease.JobId, new HlsIdentification("another lease", Sha('a'), ProbeJson, environment.Root), CancellationToken));

        var response = await taskCenter.IdentifyAsync(lease.JobId, new HlsIdentification(lease.LeaseToken, Sha('a').ToUpperInvariant(), ProbeJson, environment.Root), CancellationToken);

        Assert.NotNull(response);
        Assert.False(response.Done);
        Assert.Equal(["v1080", "v720", "a1", "preview"], response.Plan!.Renditions.Select(r => r.Key));
        Assert.Equal(Sha('a'), environment.Index.GetSource(item)!.Sha256);
        Assert.Equal(environment.Root, (await environment.JobAsync(lease.JobId)).Base);
    }

    [Fact]
    public async Task Identify_WithARootTheWorkerMayNotUse_IsRefused()
    {
        var (environment, taskCenter) = Create();
        await environment.AddSourceAsync();
        var lease = (await taskCenter.PollAsync(new HlsWorkerRequest("worker", WorkerId), CancellationToken))!.Lease;

        foreach (var root in new[] { null, " ", "relative/hls", "ftp://bucket/hls", Path.Combine(environment.TempDirectory, "missing"), _library })
        {
            await Assert.ThrowsAsync<ArgumentException>(
                () => taskCenter.IdentifyAsync(lease.JobId, new HlsIdentification(lease.LeaseToken, Sha('a'), ProbeJson, root!), CancellationToken));
        }

        Assert.Null((await environment.JobAsync(lease.JobId)).Plan);
    }

    [Fact]
    public async Task CheckBase_AcceptsADirectoryOnAWorker_InItsNormalForm()
    {
        var (environment, taskCenter) = Create();
        var worker = Guid.NewGuid();
        var root = HlsWorkerLocalAddress.Format(worker, @"D:\work\hls\");
        await environment.AddSourceAsync();
        var lease = (await taskCenter.PollAsync(new HlsWorkerRequest("worker", WorkerId), CancellationToken))!.Lease;

        Assert.Equal(new HlsStoreTestResult(true, true, null), await taskCenter.CheckBaseAsync(root, CancellationToken));
        Assert.NotNull(await taskCenter.IdentifyAsync(lease.JobId, new HlsIdentification(lease.LeaseToken, Sha('a'), ProbeJson, root), CancellationToken));
        Assert.Equal(HlsWorkerLocalAddress.Format(worker, @"D:\work\hls"), (await environment.JobAsync(lease.JobId)).Base);
    }

    [Fact]
    public async Task CheckBase_AcceptsAnExistingLocalDirectory_AndTellsWhetherItHasTheMarker()
    {
        var (environment, taskCenter) = Create();

        Assert.Equal(new HlsStoreTestResult(true, false, null), await taskCenter.CheckBaseAsync(environment.Root, CancellationToken));
        await File.WriteAllTextAsync(Path.Combine(environment.Root, HlsStorePaths.MarkerName), string.Empty, CancellationToken);
        Assert.Equal(new HlsStoreTestResult(true, true, null), await taskCenter.CheckBaseAsync(environment.Root, CancellationToken));
    }

    [Fact]
    public async Task CheckBase_RefusesALocalRootThatOverlapsALibrary_OrTheDataOfTheServer()
    {
        var (environment, taskCenter) = Create();
        var inLibrary = Directory.CreateDirectory(Path.Combine(_library, "hls")).FullName;
        var holdingLibrary = Path.GetDirectoryName(_library)!;
        var inData = Directory.CreateDirectory(Path.Combine(_data, "hls")).FullName;

        foreach (var root in new[] { _library, inLibrary, holdingLibrary, inData, Path.Combine(environment.TempDirectory, "missing"), "relative", new string('a', 1025) })
        {
            var result = await taskCenter.CheckBaseAsync(root, CancellationToken);
            Assert.False(result.Success, root);
            Assert.NotNull(result.Message);
        }
    }

    [Fact]
    public async Task CheckBase_RefusesARootInALibraryAtTheRootOfTheFileSystem()
    {
        var (environment, taskCenter) = Create(library: Path.GetPathRoot(Path.GetTempPath()));

        Assert.False((await taskCenter.CheckBaseAsync(environment.Root, CancellationToken)).Success);
    }

    [Fact]
    public async Task CheckBase_RefusesARootThatLostWhatWasPublishedInIt()
    {
        var (environment, taskCenter) = Create();
        await environment.PublishAsync(Sha('a'));

        // The mount is lost: what is left is the empty mount point, which has neither the marker nor what was published.
        Directory.Delete(environment.Root, true);
        Directory.CreateDirectory(environment.Root);

        Assert.False((await taskCenter.CheckBaseAsync(environment.Root, CancellationToken)).Success);
        Assert.Contains(environment.Root, (await taskCenter.GetStatusAsync(CancellationToken)).StoreProblems.Select(p => p.Base));
    }

    [Fact]
    public async Task Identify_OfAContentWithNothingToEncode_IsDone()
    {
        var (environment, taskCenter) = Create();
        await environment.PublishAsync(Sha('a'));
        await environment.AddSourceAsync();
        var lease = (await taskCenter.PollAsync(new HlsWorkerRequest("worker", WorkerId), CancellationToken))!.Lease;

        var response = await taskCenter.IdentifyAsync(lease.JobId, new HlsIdentification(lease.LeaseToken, Sha('a'), ProbeJson, environment.Root), CancellationToken);

        Assert.Equal(new HlsIdentifyResponse(true, null), response);
    }

    [Fact]
    public void Libraries_AreThoseChosen_WithTheirQualities()
    {
        var (environment, taskCenter) = Create();

        Assert.Equal([new HlsWorkerLibrary(LibraryId, "Movies", [1080, 720])], taskCenter.GetLibraries(), new LibraryComparer());
        environment.Save(new HlsOptions { Enabled = true, Qualities = [720, 1080], LibraryQualities = [new() { LibraryId = LibraryId, Qualities = [720] }] });
        Assert.Equal([720], Assert.Single(taskCenter.GetLibraries()).Qualities);
        environment.Save(new HlsOptions { Enabled = true, Libraries = [OtherLibraryId], Qualities = [1080] });
        Assert.Empty(taskCenter.GetLibraries());
    }

    [Fact]
    public async Task Eager_StartsAJobLeasedToTheWorker_ThatReadsNoFileOfTheServer()
    {
        var (environment, taskCenter) = Create();
        var request = new HlsEagerRequest("worker", WorkerId, LibraryId, Sha('a'), ProbeJson, environment.Root, [720]);

        var response = await taskCenter.StartEagerJobAsync(request, CancellationToken);

        Assert.False(response!.Done);
        Assert.Equal((300, 1), (response.Lease!.LeaseSeconds, response.Lease.Attempt));
        Assert.Equal(["v720", "a1", "preview"], response.Plan!.Renditions.Select(r => r.Key));
        Assert.Equal(new HlsJobInput(null, null), await taskCenter.GetInputAsync(response.Lease.JobId, response.Lease.LeaseToken, CancellationToken));
        var again = await taskCenter.IdentifyAsync(response.Lease.JobId, new HlsIdentification(response.Lease.LeaseToken, Sha('a'), ProbeJson, environment.Root), CancellationToken);
        Assert.Equal(JsonSerializer.Serialize(response.Plan), JsonSerializer.Serialize(again!.Plan));
        Assert.Null(await taskCenter.StartEagerJobAsync(request with { Qualities = [1080] }, CancellationToken));
    }

    [Fact]
    public async Task Eager_OfAnotherLibrary_OrOtherQualities_IsRefused()
    {
        var (environment, taskCenter) = Create();
        var request = new HlsEagerRequest("worker", WorkerId, LibraryId, Sha('a'), ProbeJson, environment.Root, [720]);

        foreach (var refused in new[]
        {
            request with { LibraryId = OtherLibraryId },
            request with { Qualities = [480] },
            request with { Qualities = [] },
            request with { Qualities = null! },
            request with { Sha256 = "abc" },
            request with { ProbeJson = "{}" },
            request with { Base = "relative" },
            request with { Worker = " " }
        })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => taskCenter.StartEagerJobAsync(refused, CancellationToken));
        }

        Assert.Empty(await environment.JobsAsync());
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
        var lease = (await taskCenter.PollAsync(new HlsWorkerRequest("worker", WorkerId), CancellationToken))!.Lease;

        Assert.False(await taskCenter.FailAsync(lease.JobId, new HlsFailure("another lease", "ffmpeg failed"), CancellationToken));
        Assert.True(await taskCenter.FailAsync(lease.JobId, new HlsFailure(lease.LeaseToken, new string('e', 3000)), CancellationToken));

        var job = await environment.JobAsync(lease.JobId);
        Assert.Equal((int)HlsJobStatus.Pending, job.Status);
        Assert.Equal(new string('e', 2048), job.Error);
    }

    [Fact]
    public async Task Fail_KeepsTheDirectories_ForALease()
    {
        var (environment, taskCenter) = Create();
        var (lease, plan) = await IdentifiedAsync(environment, taskCenter);
        var before = DateTime.UtcNow;

        Assert.True(await taskCenter.FailAsync(lease.JobId, new HlsFailure(lease.LeaseToken, "ffmpeg failed"), CancellationToken));

        var deletions = await environment.DeletionsAsync();
        Assert.Equal(plan.Renditions.Select(r => r.RelativeDirectory).Order(StringComparer.Ordinal), deletions.Select(d => d.RelativeDirectory).Order(StringComparer.Ordinal));
        Assert.All(deletions, d => Assert.True(d.NotBefore >= before + Lease));
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
        var lease = (await taskCenter.PollAsync(new HlsWorkerRequest("worker", WorkerId), CancellationToken))!.Lease;
        var response = await taskCenter.IdentifyAsync(lease.JobId, new HlsIdentification(lease.LeaseToken, Sha('a'), ProbeJson, environment.Root), CancellationToken);
        return (lease, response!.Plan!);
    }

    // A new local store in a temporary directory, beside a library and the data of the server.
    private (HlsTestEnvironment Environment, HlsTaskCenter TaskCenter) Create(string? library = null)
    {
        var environment = new HlsTestEnvironment();
        _disposables.Add(environment);
        Directory.CreateDirectory(environment.Root);
        _library = library ?? Directory.CreateDirectory(Path.Combine(environment.TempDirectory, "media", "movies")).FullName;
        _data = Directory.CreateDirectory(Path.Combine(environment.TempDirectory, "data")).FullName;
        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.Setup(l => l.GetVirtualFolders()).Returns([new VirtualFolderInfo { ItemId = LibraryId.ToString("N"), Name = "Movies", Locations = [_library] }]);
        var appPaths = new Mock<IServerApplicationPaths>();
        appPaths.SetupGet(p => p.ProgramDataPath).Returns(_data);
        appPaths.SetupGet(p => p.DataPath).Returns(Path.Combine(_data, "data"));
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
            libraryManager.Object,
            appPaths.Object,
            NullLogger<HlsTaskCenter>.Instance);
        return (environment, taskCenter);
    }

    // Records hold their qualities as lists, which compare by reference.
    private sealed class LibraryComparer : IEqualityComparer<HlsWorkerLibrary>
    {
        public bool Equals(HlsWorkerLibrary? x, HlsWorkerLibrary? y)
            => x is not null && y is not null && x.Id.Equals(y.Id) && x.Name == y.Name && x.Qualities.SequenceEqual(y.Qualities);

        public int GetHashCode(HlsWorkerLibrary obj) => obj.Id.GetHashCode();
    }
}
