using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.MediaEncoding.Hls.Published;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Model.Hls;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static Jellyfin.Server.Implementations.Tests.Hls.HlsTestEnvironment;

namespace Jellyfin.Server.Implementations.Tests.Hls;

public sealed class HlsJobStoreTests : IDisposable
{
    private const string NextProfile = "next-profile";
    private const int MaxAttempts = 3;

    // HlsRenditionPlanner.GetDesired(Probe, Qualities), in planner order, and in the order the store lists them.
    private static readonly string[] _plannedKeys = ["v1080", "v720", "a1", "s2", "preview"];
    private static readonly string[] _sortedKeys = ["a1", "preview", "s2", "v1080", "v720"];

    private readonly HlsTestEnvironment _environment = new();
    private readonly HlsJobStore _store;
    private DateTime _now = new(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);

    public HlsJobStoreTests()
    {
        _store = _environment.Store;
    }

    public void Dispose() => _environment.Dispose();

    // ---- Sources

    [Fact]
    public async Task UpsertSources_ReportsWhatChangedForEachFileInInputOrder()
    {
        var item = await AddSourceAsync();
        var (lease, _) = await ClaimAndPlanAsync(Sha('a'));
        var other = Guid.NewGuid();

        var results = await _store.UpsertSourcesAsync(
            [LibraryFile(item, OtherLibraryId), LibraryFile(other), LibraryFile(item) with { Length = 2000 }],
            _now,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new HlsUpsertResult(item, HlsSourceChange.Unchanged, Sha('a')),
                new HlsUpsertResult(other, HlsSourceChange.Inserted, null),
                new HlsUpsertResult(item, HlsSourceChange.ContentChanged, Sha('a'))
            ],
            results);
        var source = await _store.GetSourceAsync(item, TestContext.Current.CancellationToken);
        Assert.Equal(LibraryFile(item) with { Length = 2000 }, source!.File);
        Assert.Null(source.Sha256);

        // The running job of the item is left alone; the new file gets its job.
        Assert.Equal(lease.Id, (await SourceJobAsync(item)).Id);
        Assert.Equal((int)HlsJobStatus.Running, (await SourceJobAsync(item)).Status);
        Assert.Equal((int)HlsJobStatus.Pending, (await SourceJobAsync(other)).Status);
        Assert.Equal(new[] { item, other }.Order(), (await _store.GetSourcesAsync(TestContext.Current.CancellationToken)).Select(s => s.File.ItemId).Order());
    }

    [Fact]
    public async Task RemoveSource_ReturnsTheHashTheFileHad_AndCancelsItsPendingJob()
    {
        var pending = await AddSourceAsync();
        Assert.Null(await _store.RemoveSourceAsync(pending, _now, TestContext.Current.CancellationToken));
        Assert.Equal((int)HlsJobStatus.Cancelled, (await SourceJobAsync(pending)).Status);

        var (item, lease, _) = await EncodeAndPublishAsync(Sha('a'));
        Assert.Equal([item], (await _store.GetSourcesByShaAsync(Sha('a'), TestContext.Current.CancellationToken)).Select(s => s.File.ItemId));
        Assert.Equal(Sha('a'), await _store.RemoveSourceAsync(item, _now, TestContext.Current.CancellationToken));
        Assert.Null(await _store.GetSourceAsync(item, TestContext.Current.CancellationToken));
        Assert.Empty(await _store.GetSourcesByShaAsync(Sha('a'), TestContext.Current.CancellationToken));
        Assert.Equal((int)HlsJobStatus.Complete, (await JobAsync(lease.Id)).Status);
    }

    [Fact]
    public async Task ASourceJobIsCreatedAgainOnlyOnceItIsCompleteOrCancelledWithoutALease()
    {
        var item = await AddSourceAsync();
        var lease = (await ClaimAsync())!;
        Assert.Equal(HlsIdentifyOutcome.Done, (await IdentifyAsync(lease, Sha('a'), eligible: _ => false)).Outcome);
        Assert.Equal((int)HlsJobStatus.Complete, (await JobAsync(lease.Id)).Status);

        // A complete job is created again with a new id; a pending one is kept.
        await ChangeFileAsync(item, 2000);
        var second = await SourceJobAsync(item);
        Assert.NotEqual(lease.Id, second.Id);
        Assert.Equal((int)HlsJobStatus.Pending, second.Status);
        await ChangeFileAsync(item, 3000);
        Assert.Equal(second.Id, (await SourceJobAsync(item)).Id);

        // A running job, and a cancelled one whose worker still holds the lease, are kept.
        var running = (await ClaimAsync())!;
        await ChangeFileAsync(item, 4000);
        Assert.Equal(second.Id, (await SourceJobAsync(item)).Id);
        Assert.True(await _store.CancelJobAsync(running.Id, _now, TestContext.Current.CancellationToken));
        await ChangeFileAsync(item, 5000);
        Assert.Equal(second.Id, (await SourceJobAsync(item)).Id);

        Assert.True(await ReleaseAsync(running));
        await ChangeFileAsync(item, 6000);
        var third = await SourceJobAsync(item);
        Assert.NotEqual(second.Id, third.Id);
        Assert.Equal((int)HlsJobStatus.Pending, third.Status);
    }

    [Fact]
    public async Task EnsureSourceJobs_CreatesTheJobOfAFileThatChangedWhileItsJobRan()
    {
        var item = await AddSourceAsync();
        var (lease, plan) = await ClaimAndPlanAsync(Sha('a'));
        await ChangeFileAsync(item, 2000);
        await PublishAsync(lease, plan);
        Assert.Equal(lease.Id, (await SourceJobAsync(item)).Id);

        await _store.EnsureSourceJobsAsync(_now, TestContext.Current.CancellationToken);

        var job = await SourceJobAsync(item);
        Assert.NotEqual(lease.Id, job.Id);
        Assert.Equal((int)HlsJobStatus.Pending, job.Status);

        // The outputs the previous job published stay under its id.
        Assert.All(await _store.GetPublishedManifestsAsync(Sha('a'), TestContext.Current.CancellationToken), m => Assert.Contains(lease.LeaseToken, m.RelativeDirectory, StringComparison.Ordinal));
        Assert.Equal(_sortedKeys, (await _store.GetPublishedManifestsAsync(null, TestContext.Current.CancellationToken)).Select(m => m.Key));
    }

    // ---- Claim and lease

    [Fact]
    public async Task ParallelWorkersNeverReceiveTheSameJob()
    {
        for (var i = 0; i < 10; i++)
        {
            await AddSourceAsync();
        }

        var leases = await Task.WhenAll(Enumerable.Range(0, 20).Select(i => ClaimAsync("worker" + i.ToString(CultureInfo.InvariantCulture))));

        var claimed = leases.OfType<HlsJobLease>().ToList();
        Assert.Equal(10, claimed.Count);
        Assert.Equal(10, claimed.Select(l => l.Id).Distinct().Count());
        Assert.Equal(10, claimed.Select(l => l.LeaseToken).Distinct().Count());
    }

    [Fact]
    public async Task Claim_CancelsSourceJobsOutsideTheEnabledLibraries()
    {
        var other = await AddSourceAsync(OtherLibraryId);
        var item = await AddSourceAsync();

        var lease = await ClaimAsync(eligible: l => l.Equals(LibraryId));

        Assert.Equal(item, lease!.Input.ItemId);
        Assert.Equal((int)HlsJobStatus.Cancelled, (await SourceJobAsync(other)).Status);

        // The file keeps no hash, so its job comes back once its library is enabled.
        await _store.EnsureSourceJobsAsync(_now, TestContext.Current.CancellationToken);
        Assert.Equal((int)HlsJobStatus.Pending, (await SourceJobAsync(other)).Status);
    }

    [Fact]
    public async Task ALeaseWorksOnlyWithItsTokenUntilItExpires()
    {
        var item = await AddSourceAsync();
        var lease = (await ClaimAsync())!;
        Assert.Equal(HlsJobKind.Source, lease.Kind);
        Assert.Equal(LibraryFile(item), lease.Input);
        Assert.Equal(1, lease.Attempt);
        Assert.Equal(_now + Lease, lease.LeaseExpires);
        Assert.Null(lease.Plan);
        Assert.Equal("worker", (await JobAsync(lease.Id)).Worker);

        Assert.False(await _store.HeartbeatAsync(lease.Id, "other", Lease, _now, TestContext.Current.CancellationToken));
        Assert.Null(await _store.GetLeaseAsync(lease.Id, "other", _now, TestContext.Current.CancellationToken));

        _now += TimeSpan.FromMinutes(4);
        Assert.True(await _store.HeartbeatAsync(lease.Id, lease.LeaseToken, Lease, _now, TestContext.Current.CancellationToken));
        Assert.Equal(_now + Lease, (await _store.GetLeaseAsync(lease.Id, lease.LeaseToken, _now, TestContext.Current.CancellationToken))!.LeaseExpires);

        _now += Lease;
        Assert.False(await _store.HeartbeatAsync(lease.Id, lease.LeaseToken, Lease, _now, TestContext.Current.CancellationToken));
        Assert.Null(await _store.GetLeaseAsync(lease.Id, lease.LeaseToken, _now, TestContext.Current.CancellationToken));
        Assert.Equal(HlsIdentifyOutcome.LeaseLost, (await IdentifyAsync(lease, Sha('a'))).Outcome);
    }

    // ---- Identify

    [Fact]
    public async Task Identify_RecordsTheContentAndPlansEveryRendition()
    {
        var item = await AddSourceAsync();
        var lease = (await ClaimAsync())!;

        var result = await IdentifyAsync(lease, Sha('A'));

        Assert.Equal(HlsIdentifyOutcome.Planned, result.Outcome);
        Assert.False(result.KeptStoredProbe);
        var plan = result.Plan!;
        var mediaKey = new string('a', 32);
        var token = lease.LeaseToken;
        Assert.Equal(Sha('a'), plan.Sha256);
        Assert.Equal(mediaKey, plan.MediaKey);
        Assert.Equal(Profile, plan.Profile);
        Assert.Equal(Json(Probe), Json(plan.Probe));
        Assert.Equal(6, plan.SegmentSeconds);
        Assert.Equal(2, plan.KeyframeSeconds);
        Assert.Equal(48000, plan.AudioSampleRate);
        Assert.Equal(10, plan.ExpectedSegments);
        Assert.Equal(_plannedKeys, plan.Renditions.Select(r => r.Key));
        Assert.Equal(
            [$"{mediaKey}/video/1080/{token}", $"{mediaKey}/video/720/{token}", $"{mediaKey}/audio/1/{token}", $"{mediaKey}/subtitles/2/{token}", $"{mediaKey}/preview/{token}"],
            plan.Renditions.Select(r => r.RelativeDirectory));
        Assert.Equal(2, plan.Renditions[2].Channels);
        Assert.Equal(1, plan.Renditions[2].SourceIndex);
        Assert.Equal(720, plan.Renditions[1].Video!.Height);

        Assert.Equal(Sha('a'), (await _store.GetSourceAsync(item, TestContext.Current.CancellationToken))!.Sha256);
        var media = (await _store.GetMediaAsync(Sha('a'), TestContext.Current.CancellationToken))!;
        Assert.Equal(mediaKey, media.MediaKey);
        Assert.Equal(LibraryId, media.LibraryId);
        Assert.Null(media.DateOrphaned);
        Assert.Equal([Sha('a')], await _store.GetMediaShasAsync(TestContext.Current.CancellationToken));
        var renditions = await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken);
        Assert.Equal(_sortedKeys, renditions.Select(r => r.Key));
        Assert.All(renditions, r => Assert.Equal((HlsRenditionState.Missing, lease.Id), (r.State, r.JobId)));
        Assert.Equal(Sha('a'), (await JobAsync(lease.Id)).Sha256);
        Assert.Equal(Json(plan), Json((await _store.GetLeaseAsync(lease.Id, lease.LeaseToken, _now, TestContext.Current.CancellationToken))!.Plan!));
    }

    [Fact]
    public async Task Identify_RetriedReturnsThePlan_AndAnotherHashOrRootIsRefused()
    {
        await AddSourceAsync();
        var (lease, plan) = await ClaimAndPlanAsync(Sha('a'));

        var again = await IdentifyAsync(lease, Sha('a'));

        Assert.Equal(HlsIdentifyOutcome.Planned, again.Outcome);
        Assert.Equal(Json(plan), Json(again.Plan!));
        Assert.Equal(_environment.Root, (await JobAsync(lease.Id)).Base);
        await Assert.ThrowsAsync<ArgumentException>(() => IdentifyAsync(lease, Sha('a'), root: "s3://media/hls"));
        await Assert.ThrowsAsync<ArgumentException>(() => IdentifyAsync(lease, Sha('b')));
        await Assert.ThrowsAsync<ArgumentException>(() => IdentifyAsync(lease, "not a hash"));
    }

    [Fact]
    public async Task Identify_ContentThatAnotherJobEncodesOrPublished_IsDone()
    {
        var first = await AddSourceAsync();
        var second = await AddSourceAsync();
        var (lease, plan) = await ClaimAndPlanAsync(Sha('a'));
        Assert.Equal(first, lease.Input.ItemId);

        var duplicate = (await ClaimAsync())!;
        Assert.Equal(HlsIdentifyOutcome.Done, (await IdentifyAsync(duplicate, Sha('a'))).Outcome);
        Assert.Equal((int)HlsJobStatus.Complete, (await JobAsync(duplicate.Id)).Status);
        Assert.Equal(Sha('a'), (await _store.GetSourceAsync(second, TestContext.Current.CancellationToken))!.Sha256);
        Assert.All(await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken), r => Assert.Equal(lease.Id, r.JobId));

        await PublishAsync(lease, plan);
        await AddSourceAsync();
        var third = (await ClaimAsync())!;
        Assert.Equal(HlsIdentifyOutcome.Done, (await IdentifyAsync(third, Sha('a'))).Outcome);
    }

    [Fact]
    public async Task Identify_KnownContent_KeepsItsStoredProbe()
    {
        await EncodeAndPublishAsync(Sha('a'));
        var other = Probe with { Rotation = 90, FramesPerSecond = 50 };
        await AddSourceAsync();
        var lease = (await ClaimAsync())!;

        // Under the next profile the published renditions are encoded again, from the stored probe.
        var result = await IdentifyAsync(lease, Sha('a'), NextProfile, probe: other);

        Assert.Equal(HlsIdentifyOutcome.Planned, result.Outcome);
        Assert.True(result.KeptStoredProbe);
        var plan = result.Plan!;
        Assert.Equal(Json(Probe), Json(plan.Probe));
        Assert.Equal(Json(Probe), Json((await _store.GetMediaAsync(Sha('a'), TestContext.Current.CancellationToken))!.Probe));
        Assert.Equal(25, plan.Renditions[0].Video!.FramesPerSecond);
    }

    [Fact]
    public async Task Identify_FileChangedAfterTheClaim_WaitsAgainWithoutCountingTheAttempt()
    {
        var item = await AddSourceAsync();
        var lease = (await ClaimAsync())!;
        await ChangeFileAsync(item, 2000);

        Assert.Equal(HlsIdentifyOutcome.SourceChanged, (await IdentifyAsync(lease, Sha('a'))).Outcome);

        var job = await JobAsync(lease.Id);
        Assert.Equal((int)HlsJobStatus.Pending, job.Status);
        Assert.Equal(0, job.Attempt);
        Assert.Null(job.Input);
        Assert.Null(job.LeaseToken);
        Assert.NotNull(job.Error);
        Assert.Null(await _store.GetMediaAsync(Sha('a'), TestContext.Current.CancellationToken));
        var again = (await ClaimAsync())!;
        Assert.Equal(lease.Id, again.Id);
        Assert.Equal(2000, again.Input.Length);
        Assert.Equal(1, again.Attempt);
    }

    [Fact]
    public async Task Identify_FileRemovedAfterTheClaim_CancelsTheSourceJob()
    {
        var item = await AddSourceAsync();
        var lease = (await ClaimAsync())!;
        await _store.RemoveSourceAsync(item, _now, TestContext.Current.CancellationToken);
        Assert.Equal((int)HlsJobStatus.Running, (await JobAsync(lease.Id)).Status);

        Assert.Equal(HlsIdentifyOutcome.SourceChanged, (await IdentifyAsync(lease, Sha('a'))).Outcome);

        Assert.Equal((int)HlsJobStatus.Cancelled, (await JobAsync(lease.Id)).Status);
        Assert.Null(await _store.GetMediaAsync(Sha('a'), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Identify_MediaJobReadingOtherContent_QueuesTheFileForASourceJob()
    {
        var (item, published, _) = await EncodeAndPublishAsync(Sha('a'));
        var id = await EnsureMediaJobAsync(Sha('a'), NextProfile);
        var lease = (await ClaimAsync())!;
        Assert.Equal((id, HlsJobKind.Media, item), (lease.Id, lease.Kind, lease.Input.ItemId));
        _now += TimeSpan.FromSeconds(1);

        Assert.Equal(HlsIdentifyOutcome.SourceChanged, (await IdentifyAsync(lease, Sha('b'), NextProfile)).Outcome);

        Assert.Null((await _store.GetSourceAsync(item, TestContext.Current.CancellationToken))!.Sha256);
        var job = await JobAsync(lease.Id);
        Assert.Equal((int)HlsJobStatus.Pending, job.Status);
        Assert.Equal(0, job.Attempt);
        var sourceJob = await SourceJobAsync(item);
        Assert.NotEqual(published.Id, sourceJob.Id);
        Assert.Equal((int)HlsJobStatus.Pending, sourceJob.Status);

        // The content has no file left: the next claim cancels the media job, releasing its renditions, and takes the source job.
        Assert.Equal(sourceJob.Id, (await ClaimAsync())!.Id);
        Assert.Equal((int)HlsJobStatus.Cancelled, (await JobAsync(lease.Id)).Status);
        Assert.All(await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken), r => Assert.Null(r.JobId));
    }

    [Fact]
    public async Task Identify_SourceJobWhoseFileBecameOtherContent_GivesBackTheRenditionsOfTheOldOne()
    {
        var item = await AddSourceAsync();
        var (lease, _) = await ClaimAndPlanAsync(Sha('a'));
        Assert.True(await ReleaseAsync(lease));
        await ChangeFileAsync(item, 2000);

        for (var attempt = 2; attempt <= MaxAttempts; attempt++)
        {
            (lease, _) = await ClaimAndPlanAsync(Sha('b'));
            Assert.All(await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken), r => Assert.Null(r.JobId));
            Assert.True(await ReleaseAsync(lease));
        }

        // Only the content the job failed on fails with it.
        Assert.Equal((int)HlsJobStatus.Failed, (await JobAsync(lease.Id)).Status);
        Assert.All(await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken), r => Assert.Equal(HlsRenditionState.Missing, r.State));
        Assert.All(await _store.GetRenditionsAsync(Sha('b'), TestContext.Current.CancellationToken), r => Assert.Equal(HlsRenditionState.Failed, r.State));
        Assert.Equal([Sha('a')], await _store.GetShasNeedingJobsAsync(Profile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Identify_LibraryNoLongerEnabled_RecordsTheContentAndCompletesTheJob()
    {
        var item = await AddSourceAsync();
        var lease = (await ClaimAsync())!;

        Assert.Equal(HlsIdentifyOutcome.Done, (await IdentifyAsync(lease, Sha('a'), eligible: _ => false)).Outcome);

        Assert.Equal((int)HlsJobStatus.Complete, (await JobAsync(lease.Id)).Status);
        Assert.Equal(Sha('a'), (await _store.GetSourceAsync(item, TestContext.Current.CancellationToken))!.Sha256);
        Assert.All(await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken), r => Assert.Equal((HlsRenditionState.Missing, (Guid?)null), (r.State, r.JobId)));
        Assert.Equal([Sha('a')], await _store.GetShasNeedingJobsAsync(Profile, TestContext.Current.CancellationToken));
        Assert.Null(await EnsureMediaJobAsync(Sha('a'), Profile, eligible: _ => false));
    }

    // ---- Media jobs

    [Fact]
    public async Task EnsureMediaJob_ReusesThePendingJob_LeavesARunningOneAlone_AndStartsTheNextRunWithANewId()
    {
        var (item, _, _) = await EncodeAndPublishAsync(Sha('a'));
        Assert.Null(await EnsureMediaJobAsync(Sha('a'), Profile));

        var id = await EnsureMediaJobAsync(Sha('a'), NextProfile, item);
        Assert.NotNull(id);
        Assert.Null(await EnsureMediaJobAsync(Sha('a'), NextProfile));
        Assert.True(await _store.SyncRenditionsAsync(Sha('a'), [1080, 720, 480], _now, TestContext.Current.CancellationToken));
        Assert.Equal(id, await EnsureMediaJobAsync(Sha('a'), NextProfile));
        Assert.All(await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken), r => Assert.Equal(id, r.JobId));

        var (lease, plan) = await ClaimAndPlanAsync(Sha('a'), NextProfile, [1080, 720, 480]);
        Assert.Equal(id, lease.Id);
        Assert.Equal(HlsJobKind.Media, lease.Kind);
        Assert.Contains("v480", plan.Renditions.Select(r => r.Key));
        Assert.True(await _store.SyncRenditionsAsync(Sha('a'), [1080, 720, 480, 360], _now, TestContext.Current.CancellationToken));
        Assert.Null(await EnsureMediaJobAsync(Sha('a'), NextProfile));

        await PublishAsync(lease, plan);
        var next = await EnsureMediaJobAsync(Sha('a'), NextProfile);
        Assert.NotNull(next);
        Assert.NotEqual(id, next);
        Assert.Equal(next, Assert.Single(await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken), r => r.JobId is not null).JobId);
    }

    [Fact]
    public async Task EnsureMediaJob_NeedsAFileOfTheContentInAnEnabledLibrary()
    {
        var (item, _, _) = await EncodeAndPublishAsync(Sha('a'));

        Assert.Null(await EnsureMediaJobAsync(Sha('a'), NextProfile, eligible: _ => false));
        await _store.RemoveSourceAsync(item, _now, TestContext.Current.CancellationToken);
        Assert.Null(await EnsureMediaJobAsync(Sha('a'), NextProfile));

        Assert.Equal([Sha('a')], await _store.GetShasNeedingJobsAsync(NextProfile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Claim_MediaJobReadsItsItem_OrAnotherFileOfTheContent()
    {
        var (first, _, _) = await EncodeAndPublishAsync(Sha('a'));
        var second = await AddSourceAsync(OtherLibraryId);
        Assert.Equal(HlsIdentifyOutcome.Done, (await IdentifyAsync((await ClaimAsync())!, Sha('a'))).Outcome);
        var id = await EnsureMediaJobAsync(Sha('a'), NextProfile, second);

        var lease = (await ClaimAsync())!;
        Assert.Equal((id, second), (lease.Id, lease.Input.ItemId));
        Assert.True(await ReleaseAsync(lease));

        lease = (await ClaimAsync(eligible: l => l.Equals(LibraryId)))!;
        Assert.Equal((id, first, 2), (lease.Id, lease.Input.ItemId, lease.Attempt));
    }

    [Fact]
    public async Task Claim_CancelsAMediaJobWithoutAFile_AndReleasesItsRenditions()
    {
        var (item, _, _) = await EncodeAndPublishAsync(Sha('a'));
        var id = await EnsureMediaJobAsync(Sha('a'), NextProfile);
        await _store.RemoveSourceAsync(item, _now, TestContext.Current.CancellationToken);

        Assert.Null(await ClaimAsync());

        Assert.Equal((int)HlsJobStatus.Cancelled, (await JobAsync(id!.Value)).Status);
        Assert.All(await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken), r => Assert.Null(r.JobId));
    }

    // ---- Outputs and publishing

    [Fact]
    public async Task SubmitOutputs_RequiresAValidLeaseAndAPlan_AndChecksTheOutputsAgainstThePlan()
    {
        await AddSourceAsync();
        var lease = (await ClaimAsync())!;
        Assert.False(await SubmitAsync(lease, []));
        var plan = (await IdentifyAsync(lease, Sha('a'))).Plan!;

        Assert.False(await _store.SubmitOutputsAsync(lease.Id, "other", Outputs(plan), _now, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentException>(() => SubmitAsync(lease, Outputs(plan)[1..]));
        Assert.Equal((int)HlsJobStatus.Running, (await JobAsync(lease.Id)).Status);

        Assert.True(await SubmitAsync(lease, Outputs(plan)));

        var job = await JobAsync(lease.Id);
        Assert.Equal((int)HlsJobStatus.Verifying, job.Status);
        Assert.Null(job.LeaseExpires);
        Assert.False(await SubmitAsync(lease, Outputs(plan)));
        Assert.False(await _store.HeartbeatAsync(lease.Id, lease.LeaseToken, Lease, _now, TestContext.Current.CancellationToken));
        var verifying = Assert.Single(await _store.GetVerifyingJobsAsync(10, [], TestContext.Current.CancellationToken));
        Assert.Equal((lease.Id, Sha('a'), _environment.Root, 1), (verifying.Id, verifying.Sha256, verifying.Base, verifying.Attempt));
        Assert.Equal(_sortedKeys, verifying.Outputs.Select(o => o.Key));
        Assert.All(verifying.Outputs, o => Assert.Equal(plan.Renditions.Single(r => r.Key == o.Key).RelativeDirectory, o.RelativeDirectory));
        Assert.Equal(11, verifying.Outputs.Single(o => o.Key == "v1080").Files.Count);
    }

    [Fact]
    public async Task Publish_InAnotherRoot_MovesTheRenditionsThere_AndDeletesTheReplacedOnesFromTheOldRoot()
    {
        const string Other = "s3://media/hls";
        await EncodeAndPublishAsync(Sha('a'));
        await EnsureMediaJobAsync(Sha('a'), NextProfile);
        var next = await ClaimAsync();
        var nextPlan = (await IdentifyAsync(next!, Sha('a'), NextProfile, root: Other)).Plan!;
        await PublishAsync(next!, nextPlan);

        Assert.All(await _store.GetPublishedRenditionsAsync(TestContext.Current.CancellationToken), r => Assert.Equal(Other, r.Base));
        Assert.All(await _store.GetPublishedManifestsAsync(null, TestContext.Current.CancellationToken), m => Assert.Equal(Other, m.Base));
        Assert.All(await DeletionsAsync(), d => Assert.Equal(_environment.Root, d.Base));
        Assert.Equal(new[] { _environment.Root, Other }.Order(StringComparer.Ordinal), await _store.GetRootsAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await _store.GetPublishedSampleAsync(_environment.Root, false, 10, TestContext.Current.CancellationToken));
        Assert.Equal(Directories(nextPlan).Count(), (await _store.GetPublishedSampleAsync(Other, false, 10, TestContext.Current.CancellationToken)).Count);
    }

    [Fact]
    public async Task Publish_MakesOutputsVisible_AndQueuesTheDirectoriesTheyReplaceAfterTheGrace()
    {
        var (_, lease, plan) = await EncodeAndPublishAsync(Sha('a'));

        var published = await _store.GetPublishedRenditionsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(_sortedKeys, published.Select(r => r.Key));
        Assert.All(published, r => Assert.Equal((Profile, (Guid?)lease.Id, (Guid?)null, _environment.Root), (r.Profile, r.PublishedJobId, r.JobId, r.Base)));
        Assert.Equal(1080, published.Single(r => r.Key == "v1080").Summary!.Quality);
        Assert.Equal(Directories(plan), (await _store.GetPublishedManifestsAsync(null, TestContext.Current.CancellationToken)).Select(m => m.RelativeDirectory).Order(StringComparer.Ordinal));
        Assert.Equal(11, (await _store.GetPublishedManifestsAsync(Sha('a'), TestContext.Current.CancellationToken)).Single(m => m.Key == "v720").Files.Count);
        Assert.Empty(await DeletionsAsync());

        _now += TimeSpan.FromHours(1);
        await EnsureMediaJobAsync(Sha('a'), NextProfile);
        var (next, nextPlan) = await ClaimAndPlanAsync(Sha('a'), NextProfile);
        var result = await PublishAsync(next, nextPlan);

        Assert.Equal(Sha('a'), result.Sha256);
        Assert.Equal(_sortedKeys, result.PublishedKeys);
        AssertQueued(await DeletionsAsync(), Directories(plan), _now + ReplacedGrace);
        Assert.Equal(Directories(nextPlan), (await _store.GetPublishedManifestsAsync(null, TestContext.Current.CancellationToken)).Select(m => m.RelativeDirectory).Order(StringComparer.Ordinal));
        Assert.Empty(await OutputsAsync(lease.Id));
        Assert.Equal((int)HlsJobStatus.Complete, (await JobAsync(next.Id)).Status);
        Assert.All(await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken), r => Assert.Equal((NextProfile, (Guid?)null), (r.Profile, r.JobId)));
        Assert.Empty(await _store.GetShasNeedingJobsAsync(NextProfile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Publish_DiscardsTheOutputOfARenditionNoLongerWanted()
    {
        await AddSourceAsync();
        var (lease, plan) = await ClaimAndPlanAsync(Sha('a'));
        Assert.True(await _store.SyncRenditionsAsync(Sha('a'), [720], _now, TestContext.Current.CancellationToken));

        var result = await PublishAsync(lease, plan);

        Assert.Equal(["a1", "preview", "s2", "v720"], result.PublishedKeys);
        AssertQueued(await DeletionsAsync(), [plan.Renditions[0].RelativeDirectory], _now);
        Assert.Equal(["a1", "preview", "s2", "v720"], (await OutputsAsync(lease.Id)).Select(o => o.Key));
    }

    [Fact]
    public async Task FailVerification_DiscardsTheOutputs_AndRetriesTheJob()
    {
        await AddSourceAsync();
        var (lease, plan) = await ClaimAndPlanAsync(Sha('a'));
        Assert.True(await SubmitAsync(lease, Outputs(plan)));

        Assert.True(await _store.FailVerificationAsync(lease.Id, "A segment is missing.", MaxAttempts, _now, TestContext.Current.CancellationToken));

        Assert.False(await _store.FailVerificationAsync(lease.Id, "A segment is missing.", MaxAttempts, _now, TestContext.Current.CancellationToken));
        AssertQueued(await DeletionsAsync(), Directories(plan), _now);
        Assert.Empty(await OutputsAsync(lease.Id));
        Assert.Empty(await _store.GetVerifyingJobsAsync(10, [], TestContext.Current.CancellationToken));
        var job = await JobAsync(lease.Id);
        Assert.Equal(((int)HlsJobStatus.Pending, "A segment is missing."), (job.Status, job.Error));
        Assert.Null(job.Plan);
        Assert.All(await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken), r => Assert.Equal(lease.Id, r.JobId));

        var (again, againPlan) = await ClaimAndPlanAsync(Sha('a'));
        Assert.Equal((lease.Id, 2), (again.Id, again.Attempt));
        Assert.Empty(Directories(againPlan).Intersect(Directories(plan)));
    }

    // ---- Ending a lease

    [Fact]
    public async Task Release_QueuesThePlannedDirectoriesAfterTheGrace_AndRetriesTheJob()
    {
        await AddSourceAsync();
        var (lease, plan) = await ClaimAndPlanAsync(Sha('a'));
        Assert.False(await _store.ReleaseAsync(lease.Id, "other", "ffmpeg failed", MaxAttempts, Lease, _now, TestContext.Current.CancellationToken));

        Assert.True(await ReleaseAsync(lease));

        Assert.False(await ReleaseAsync(lease));
        var deletions = await DeletionsAsync();
        AssertQueued(deletions, Directories(plan), _now + Lease);
        Assert.All(deletions, d => Assert.Equal("The worker reported a failure.", d.Reason));
        var job = await JobAsync(lease.Id);
        Assert.Equal(((int)HlsJobStatus.Pending, 1, "ffmpeg failed"), (job.Status, job.Attempt, job.Error));
        Assert.Null(job.LeaseToken);
        Assert.Null(job.Input);
        Assert.All(await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken), r => Assert.Equal(lease.Id, r.JobId));
    }

    [Fact]
    public async Task ReachingTheAttemptLimitFailsForGood_UntilARebuild()
    {
        await AddSourceAsync();
        HlsJobLease? lease = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            (lease, _) = await ClaimAndPlanAsync(Sha('a'));
            Assert.Equal(attempt, lease.Attempt);
            Assert.True(await ReleaseAsync(lease));
        }

        Assert.Equal((int)HlsJobStatus.Failed, (await JobAsync(lease!.Id)).Status);
        Assert.All(await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken), r => Assert.Equal((HlsRenditionState.Failed, (Guid?)null), (r.State, r.JobId)));
        Assert.Null(await ClaimAsync());
        Assert.Empty(await _store.GetShasNeedingJobsAsync(Profile, TestContext.Current.CancellationToken));
        Assert.Null(await EnsureMediaJobAsync(Sha('a'), Profile));

        await _store.ResetForRebuildAsync(_now, TestContext.Current.CancellationToken);

        Assert.All(await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken), r => Assert.Equal(HlsRenditionState.Missing, r.State));
        Assert.Equal([Sha('a')], await _store.GetShasNeedingJobsAsync(Profile, TestContext.Current.CancellationToken));
        var id = await EnsureMediaJobAsync(Sha('a'), Profile);
        var again = (await ClaimAsync())!;
        Assert.Equal((id, 1), (again.Id, again.Attempt));
    }

    [Fact]
    public async Task ASourceJobThatFailedForGood_IsCreatedAgainAfterARebuild()
    {
        var item = await AddSourceAsync();
        HlsJobLease? lease = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            lease = (await ClaimAsync())!;
            Assert.True(await ReleaseAsync(lease));
        }

        Assert.Equal((int)HlsJobStatus.Failed, (await JobAsync(lease!.Id)).Status);
        await _store.EnsureSourceJobsAsync(_now, TestContext.Current.CancellationToken);
        Assert.Equal(lease.Id, (await SourceJobAsync(item)).Id);

        await _store.ResetForRebuildAsync(_now, TestContext.Current.CancellationToken);
        Assert.Equal((int)HlsJobStatus.Cancelled, (await JobAsync(lease.Id)).Status);
        await _store.EnsureSourceJobsAsync(_now, TestContext.Current.CancellationToken);

        var job = await SourceJobAsync(item);
        Assert.NotEqual(lease.Id, job.Id);
        Assert.Equal(((int)HlsJobStatus.Pending, 0), (job.Status, job.Attempt));
    }

    [Fact]
    public async Task PublishedRenditionsWhoseReencodeFailed_StayPlayableButUnscheduledUntilARebuild()
    {
        await EncodeAndPublishAsync(Sha('a'));
        var id = await EnsureMediaJobAsync(Sha('a'), NextProfile);
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var (lease, _) = await ClaimAndPlanAsync(Sha('a'), NextProfile);
            Assert.True(await ReleaseAsync(lease));
        }

        Assert.Equal((int)HlsJobStatus.Failed, (await JobAsync(id!.Value)).Status);
        var published = await _store.GetPublishedRenditionsAsync(TestContext.Current.CancellationToken);
        Assert.Equal(_sortedKeys, published.Select(r => r.Key));
        Assert.All(published, r => Assert.Equal((Profile, id), (r.Profile, r.JobId)));
        Assert.Empty(await _store.GetShasNeedingJobsAsync(NextProfile, TestContext.Current.CancellationToken));

        await _store.ResetForRebuildAsync(_now, TestContext.Current.CancellationToken);

        Assert.Equal([Sha('a')], await _store.GetShasNeedingJobsAsync(NextProfile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReapingAnExpiredLease_DelaysTheDeletionByTheGrace()
    {
        await AddSourceAsync();
        var (lease, plan) = await ClaimAndPlanAsync(Sha('a'));
        var grace = TimeSpan.FromMinutes(10);
        Assert.Equal(0, await _store.ReapExpiredLeasesAsync(MaxAttempts, grace, _now, TestContext.Current.CancellationToken));

        _now += Lease;
        Assert.Equal(1, await _store.ReapExpiredLeasesAsync(MaxAttempts, grace, _now, TestContext.Current.CancellationToken));

        AssertQueued(await DeletionsAsync(), Directories(plan), _now + grace);
        var job = await JobAsync(lease.Id);
        Assert.Equal(((int)HlsJobStatus.Pending, "The lease expired."), (job.Status, job.Error));
        Assert.Equal(0, await _store.ReapExpiredLeasesAsync(MaxAttempts, grace, _now, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ACancelledRunningJob_IsLeftToItsWorkerUntilItReleasesTheLease()
    {
        await EncodeAndPublishAsync(Sha('a'));
        var id = await EnsureMediaJobAsync(Sha('a'), NextProfile);
        var (lease, plan) = await ClaimAndPlanAsync(Sha('a'), NextProfile);

        Assert.True(await _store.CancelJobAsync(id!.Value, _now, TestContext.Current.CancellationToken));

        Assert.False(await _store.HeartbeatAsync(lease.Id, lease.LeaseToken, Lease, _now, TestContext.Current.CancellationToken));
        Assert.Equal(HlsIdentifyOutcome.LeaseLost, (await IdentifyAsync(lease, Sha('a'), NextProfile)).Outcome);
        Assert.Equal([Sha('a')], await _store.GetShasNeedingJobsAsync(NextProfile, TestContext.Current.CancellationToken));
        Assert.Null(await EnsureMediaJobAsync(Sha('a'), NextProfile));
        Assert.Empty(await DeletionsAsync());

        Assert.True(await ReleaseAsync(lease));

        AssertQueued(await DeletionsAsync(), Directories(plan), _now + Lease);
        var job = await JobAsync(lease.Id);
        Assert.Equal((int)HlsJobStatus.Cancelled, job.Status);
        Assert.Null(job.LeaseExpires);
        var next = await EnsureMediaJobAsync(Sha('a'), NextProfile);
        Assert.NotNull(next);
        Assert.NotEqual(id, next);
    }

    [Fact]
    public async Task ReapingACancelledJob_OnlyClearsItsLease()
    {
        await AddSourceAsync();
        var (lease, plan) = await ClaimAndPlanAsync(Sha('a'));
        Assert.True(await _store.CancelJobAsync(lease.Id, _now, TestContext.Current.CancellationToken));

        _now += Lease;
        Assert.Equal(1, await _store.ReapExpiredLeasesAsync(MaxAttempts, ReplacedGrace, _now, TestContext.Current.CancellationToken));

        var job = await JobAsync(lease.Id);
        Assert.Equal(((int)HlsJobStatus.Cancelled, 1, (string?)null, (DateTime?)null), (job.Status, job.Attempt, job.Error, job.LeaseExpires));
        AssertQueued(await DeletionsAsync(), Directories(plan), _now + ReplacedGrace);
    }

    [Fact]
    public async Task CancelAllJobs_CancelsPendingAndRunningJobs_ButNotVerifyingOnes()
    {
        await AddSourceAsync();
        await AddSourceAsync();
        var pending = await AddSourceAsync();
        var (verifying, verifyingPlan) = await ClaimAndPlanAsync(Sha('a'));
        Assert.True(await SubmitAsync(verifying, Outputs(verifyingPlan)));
        var (running, _) = await ClaimAndPlanAsync(Sha('b'));

        await _store.CancelAllJobsAsync(_now, TestContext.Current.CancellationToken);

        Assert.Equal((int)HlsJobStatus.Verifying, (await JobAsync(verifying.Id)).Status);
        var job = await JobAsync(running.Id);
        Assert.Equal(((int)HlsJobStatus.Cancelled, running.LeaseToken), (job.Status, job.LeaseToken));
        Assert.Equal((int)HlsJobStatus.Cancelled, (await SourceJobAsync(pending)).Status);
        Assert.All(await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken), r => Assert.Equal(verifying.Id, r.JobId));
        Assert.All(await _store.GetRenditionsAsync(Sha('b'), TestContext.Current.CancellationToken), r => Assert.Null(r.JobId));
    }

    [Fact]
    public async Task CancelJob_CancelsOnlyAJobThatHasNotFinished_AndDiscardsTheOutputsOfAVerifyingOne()
    {
        var (_, published, _) = await EncodeAndPublishAsync(Sha('a'));
        Assert.False(await _store.CancelJobAsync(published.Id, _now, TestContext.Current.CancellationToken));
        Assert.False(await _store.CancelJobAsync(Guid.NewGuid(), _now, TestContext.Current.CancellationToken));
        await AddSourceAsync();
        var (lease, plan) = await ClaimAndPlanAsync(Sha('b'));
        Assert.True(await SubmitAsync(lease, Outputs(plan)));

        Assert.True(await _store.CancelJobAsync(lease.Id, _now, TestContext.Current.CancellationToken));

        var deletions = await DeletionsAsync();
        AssertQueued(deletions, Directories(plan), _now);
        Assert.All(deletions, d => Assert.Equal("The job was cancelled.", d.Reason));
        Assert.Empty(await _store.GetVerifyingJobsAsync(10, [], TestContext.Current.CancellationToken));
        Assert.Null(await _store.PublishAsync(lease.Id, ReplacedGrace, _now, TestContext.Current.CancellationToken));
        Assert.All(await _store.GetRenditionsAsync(Sha('b'), TestContext.Current.CancellationToken), r => Assert.Equal((HlsRenditionState.Missing, (Guid?)null), (r.State, r.JobId)));

        // Nothing is left to release or reap: submitting the outputs ended the lease.
        Assert.Equal(0, await _store.ReapExpiredLeasesAsync(MaxAttempts, ReplacedGrace, _now + Lease, TestContext.Current.CancellationToken));
        Assert.NotNull(await EnsureMediaJobAsync(Sha('b'), Profile));
    }

    // ---- Orphans

    [Fact]
    public async Task OrphanMedia_IsDeletedOnlyAfterTheRetention_WithEverythingItOwns()
    {
        var (item, lease, plan) = await EncodeAndPublishAsync(Sha('a'));
        await _store.RefreshOrphansAsync(_now, TestContext.Current.CancellationToken);
        Assert.Null((await _store.GetMediaAsync(Sha('a'), TestContext.Current.CancellationToken))!.DateOrphaned);
        var pending = await EnsureMediaJobAsync(Sha('a'), NextProfile);

        await _store.RemoveSourceAsync(item, _now, TestContext.Current.CancellationToken);
        var orphaned = _now;
        await _store.RefreshOrphansAsync(_now, TestContext.Current.CancellationToken);
        _now += TimeSpan.FromHours(1);
        await _store.RefreshOrphansAsync(_now, TestContext.Current.CancellationToken);
        Assert.Equal(orphaned, (await _store.GetMediaAsync(Sha('a'), TestContext.Current.CancellationToken))!.DateOrphaned);
        Assert.Empty(await _store.DeleteExpiredOrphansAsync(orphaned - TimeSpan.FromSeconds(1), _now, TestContext.Current.CancellationToken));

        Assert.Equal([Sha('a')], await _store.DeleteExpiredOrphansAsync(orphaned, _now, TestContext.Current.CancellationToken));

        Assert.Null(await _store.GetMediaAsync(Sha('a'), TestContext.Current.CancellationToken));
        Assert.Empty(await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken));
        Assert.Empty(await _store.GetPublishedManifestsAsync(null, TestContext.Current.CancellationToken));
        Assert.Empty(await OutputsAsync(lease.Id));
        Assert.DoesNotContain(await _store.ListJobsAsync(100, TestContext.Current.CancellationToken), j => j.Id.Equals(lease.Id) || pending.Equals(j.Id));
        AssertQueued(await DeletionsAsync(), Directories(plan), _now);
    }

    [Fact]
    public async Task OrphansStillInUseAreKept_AndAFileMakesThemNoOrphansAgain()
    {
        var (item, _, _) = await EncodeAndPublishAsync(Sha('a'));
        await EnsureMediaJobAsync(Sha('a'), NextProfile);
        await ClaimAndPlanAsync(Sha('a'), NextProfile);
        await _store.RemoveSourceAsync(item, _now, TestContext.Current.CancellationToken);
        await _store.RefreshOrphansAsync(_now, TestContext.Current.CancellationToken);

        Assert.Empty(await _store.DeleteExpiredOrphansAsync(_now, _now, TestContext.Current.CancellationToken));

        var other = await AddSourceAsync();
        Assert.Equal(HlsIdentifyOutcome.Done, (await IdentifyAsync((await ClaimAsync())!, Sha('a'), NextProfile)).Outcome);
        Assert.Equal([other], (await _store.GetSourcesByShaAsync(Sha('a'), TestContext.Current.CancellationToken)).Select(s => s.File.ItemId));
        Assert.Null((await _store.GetMediaAsync(Sha('a'), TestContext.Current.CancellationToken))!.DateOrphaned);
    }

    // ---- Renditions

    [Fact]
    public async Task SyncRenditions_AddsNewKeys_AndRemovesThoseNoLongerWanted()
    {
        var (_, _, plan) = await EncodeAndPublishAsync(Sha('a'));
        Assert.False(await _store.SyncRenditionsAsync(Sha('a'), Qualities, _now, TestContext.Current.CancellationToken));
        Assert.False(await _store.SyncRenditionsAsync(Sha('b'), Qualities, _now, TestContext.Current.CancellationToken));

        Assert.True(await _store.SyncRenditionsAsync(Sha('a'), [720, 480], _now, TestContext.Current.CancellationToken));

        var renditions = await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken);
        Assert.Equal(["a1", "preview", "s2", "v480", "v720"], renditions.Select(r => r.Key));
        Assert.Equal(HlsRenditionState.Missing, renditions.Single(r => r.Key == "v480").State);
        AssertQueued(await DeletionsAsync(), [plan.Renditions[0].RelativeDirectory], _now);
        Assert.DoesNotContain("v1080", (await _store.GetPublishedManifestsAsync(null, TestContext.Current.CancellationToken)).Select(m => m.Key));
    }

    [Fact]
    public async Task ShasNeedingJobs_FollowsTheDefinition()
    {
        await EncodeAndPublishAsync(Sha('a'));
        await AddSourceAsync();
        await ClaimAndPlanAsync(Sha('b'));
        await AddSourceAsync();
        Assert.Equal(HlsIdentifyOutcome.Done, (await IdentifyAsync((await ClaimAsync())!, Sha('c'), eligible: _ => false)).Outcome);

        Assert.Equal([Sha('c')], await _store.GetShasNeedingJobsAsync(Profile, TestContext.Current.CancellationToken));
        Assert.Equal([Sha('a'), Sha('c')], await _store.GetShasNeedingJobsAsync(NextProfile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RetireRendition_OnlyRetiresTheDirectoryThatWasChecked()
    {
        var (_, lease, plan) = await EncodeAndPublishAsync(Sha('a'));
        var directory = plan.Renditions[0].RelativeDirectory;

        Assert.False(await _store.RetireRenditionAsync(Sha('a'), "v1080", plan.Renditions[1].RelativeDirectory, "A segment is missing.", _now, TestContext.Current.CancellationToken));
        Assert.False(await _store.RetireRenditionAsync(Sha('a'), "v480", directory, "A segment is missing.", _now, TestContext.Current.CancellationToken));
        Assert.True(await _store.RetireRenditionAsync(Sha('a'), "v1080", directory, "A segment is missing.", _now, TestContext.Current.CancellationToken));
        Assert.False(await _store.RetireRenditionAsync(Sha('a'), "v1080", directory, "A segment is missing.", _now, TestContext.Current.CancellationToken));

        var rendition = (await _store.GetRenditionsAsync(Sha('a'), TestContext.Current.CancellationToken)).Single(r => r.Key == "v1080");
        Assert.Equal((HlsRenditionState.Missing, (string?)null, (Guid?)null), (rendition.State, rendition.RelativeDirectory, rendition.PublishedJobId));
        var deletion = Assert.Single(await DeletionsAsync());
        Assert.Equal((directory, "A segment is missing."), (deletion.RelativeDirectory, deletion.Reason));
        Assert.DoesNotContain("v1080", (await OutputsAsync(lease.Id)).Select(o => o.Key));
        Assert.Equal([Sha('a')], await _store.GetShasNeedingJobsAsync(Profile, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task PublishedShasAndSample_ListThePublishedContents()
    {
        await EncodeAndPublishAsync(Sha('b'));
        _now += TimeSpan.FromMinutes(1);
        var (_, _, plan) = await EncodeAndPublishAsync(Sha('a'));

        Assert.Equal([Sha('a'), Sha('b')], await _store.GetPublishedShasAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Directories(plan).Take(2), await _store.GetPublishedSampleAsync(_environment.Root, false, 2, TestContext.Current.CancellationToken));
        Assert.Equal(10, (await _store.GetPublishedSampleAsync(_environment.Root, false, 100, TestContext.Current.CancellationToken)).Count);
        Assert.Empty(await _store.GetPublishedSampleAsync("s3://other", false, 100, TestContext.Current.CancellationToken));
        Assert.Empty(await _store.GetPublishedSampleAsync(_environment.Root.ToUpperInvariant(), false, 100, TestContext.Current.CancellationToken));
        Assert.Equal(10, (await _store.GetPublishedSampleAsync(_environment.Root.ToUpperInvariant(), true, 100, TestContext.Current.CancellationToken)).Count);
    }

    // ---- Deletions

    [Fact]
    public async Task Deletions_AreHandedOutWhenDue_AndCanBePostponed()
    {
        await AddSourceAsync();
        var (_, plan) = await ClaimAndPlanAsync(Sha('a'));
        _now += Lease;
        await _store.ReapExpiredLeasesAsync(MaxAttempts, TimeSpan.FromMinutes(10), _now, TestContext.Current.CancellationToken);
        Assert.Empty(await _store.GetDueDeletionsAsync(_now, 10, [], TestContext.Current.CancellationToken));

        var due = await _store.GetDueDeletionsAsync(_now + TimeSpan.FromMinutes(10), 2, [], TestContext.Current.CancellationToken);

        Assert.Equal(Directories(plan).Take(2).Select(d => new HlsDeletionRecord(_environment.Root, d, 0)), due);
        await _store.PostponeDeletionAsync(due[0].RelativeDirectory, "Access denied.", _now + TimeSpan.FromHours(1), TestContext.Current.CancellationToken);
        await _store.CompleteDeletionAsync(due[1].RelativeDirectory, TestContext.Current.CancellationToken);
        await _store.CompleteDeletionAsync("unknown", TestContext.Current.CancellationToken);
        await _store.PostponeDeletionAsync("unknown", "Access denied.", _now, TestContext.Current.CancellationToken);
        Assert.Equal(Directories(plan).Skip(2), (await _store.GetDueDeletionsAsync(_now + TimeSpan.FromMinutes(10), 10, [], TestContext.Current.CancellationToken)).Select(d => d.RelativeDirectory));
        var postponed = (await DeletionsAsync()).Single(d => d.RelativeDirectory == due[0].RelativeDirectory);
        Assert.Equal((1, "Access denied.", _now + TimeSpan.FromHours(1)), (postponed.Attempts, postponed.LastError, postponed.NotBefore));
        Assert.Contains(new HlsDeletionRecord(_environment.Root, due[0].RelativeDirectory, 1), await _store.GetDueDeletionsAsync(_now + TimeSpan.FromHours(1), 10, [], TestContext.Current.CancellationToken));
    }

    // ---- Listing

    [Fact]
    public async Task ListJobs_ShowsTheMostRecentlyChangedJobsFirst_AndCountsSummarizeTheStore()
    {
        var (item, published, _) = await EncodeAndPublishAsync(Sha('a'));
        var publishedAt = _now;
        await AddSourceAsync();
        var running = (await ClaimAsync())!;
        var pending = await AddSourceAsync();

        var jobs = await _store.ListJobsAsync(10, TestContext.Current.CancellationToken);

        Assert.Equal([(await SourceJobAsync(pending)).Id, running.Id, published.Id], jobs.Select(j => j.Id));
        Assert.Equal(new HlsJobSummary(published.Id, HlsJobKind.Source, item, Sha('a'), HlsJobStatus.Complete, 1, "worker", null, publishedAt), jobs[2]);
        Assert.Single(await _store.ListJobsAsync(1, TestContext.Current.CancellationToken));
        Assert.Equal(
            new HlsJobCounts(Pending: 1, Running: 1, Verifying: 0, Complete: 1, Failed: 0, Cancelled: 0, PendingDeletions: 0, Media: 1, PublishedRenditions: _plannedKeys.Length),
            await _store.GetCountsAsync(TestContext.Current.CancellationToken));
    }

    // ---- Helpers

    private static string Json(object value) => JsonSerializer.Serialize(value);

    private static IEnumerable<string> Directories(HlsEncodePlan plan) => plan.Renditions.Select(r => r.RelativeDirectory).Order(StringComparer.Ordinal);

    private void AssertQueued(IReadOnlyList<HlsDeletion> deletions, IEnumerable<string> directories, DateTime notBefore)
    {
        Assert.Equal(directories.Order(StringComparer.Ordinal), deletions.Select(d => d.RelativeDirectory));
        Assert.All(deletions, d => Assert.Equal(notBefore, d.NotBefore));
        Assert.All(deletions, d => Assert.Equal(_environment.Root, d.Base));
    }

    // Records a new library file; its source job is created a second after every earlier one, so jobs are claimed in this order.
    private async Task<Guid> AddSourceAsync(Guid? libraryId = null)
    {
        var item = Guid.NewGuid();
        _now += TimeSpan.FromSeconds(1);
        var result = Assert.Single(await _store.UpsertSourcesAsync([LibraryFile(item, libraryId)], _now, TestContext.Current.CancellationToken));
        Assert.Equal(HlsSourceChange.Inserted, result.Change);
        return item;
    }

    private async Task ChangeFileAsync(Guid item, long length)
    {
        _now += TimeSpan.FromSeconds(1);
        var result = Assert.Single(await _store.UpsertSourcesAsync([LibraryFile(item) with { Length = length }], _now, TestContext.Current.CancellationToken));
        Assert.Equal(HlsSourceChange.ContentChanged, result.Change);
    }

    private Task<HlsJobLease?> ClaimAsync(string worker = "worker", Func<Guid, bool>? eligible = null)
        => _store.ClaimAsync(new HlsClaimRequest(worker, Lease, eligible ?? (_ => true), _now), TestContext.Current.CancellationToken);

    private Task<HlsIdentifyResult> IdentifyAsync(HlsJobLease lease, string sha, string profile = Profile, Func<Guid, bool>? eligible = null, HlsProbe? probe = null, int[]? qualities = null, string? root = null)
        => _store.IdentifyAsync(new HlsIdentifyRequest(lease.Id, lease.LeaseToken, sha, probe ?? Probe, root ?? _environment.Root, qualities ?? Qualities, profile, eligible ?? (_ => true), _now), TestContext.Current.CancellationToken);

    // Claims the oldest pending job and identifies its file as the content, which it must then encode.
    private async Task<(HlsJobLease Lease, HlsEncodePlan Plan)> ClaimAndPlanAsync(string sha, string profile = Profile, int[]? qualities = null)
    {
        var lease = await ClaimAsync();
        Assert.NotNull(lease);
        var result = await IdentifyAsync(lease, sha, profile, qualities: qualities);
        Assert.Equal(HlsIdentifyOutcome.Planned, result.Outcome);
        return (lease, result.Plan!);
    }

    private Task<bool> SubmitAsync(HlsJobLease lease, IReadOnlyList<HlsRenditionOutput> outputs)
        => _store.SubmitOutputsAsync(lease.Id, lease.LeaseToken, outputs, _now, TestContext.Current.CancellationToken);

    private async Task<HlsPublishResult> PublishAsync(HlsJobLease lease, HlsEncodePlan plan)
    {
        Assert.True(await SubmitAsync(lease, Outputs(plan)));
        var result = await _store.PublishAsync(lease.Id, ReplacedGrace, _now, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        return result;
    }

    // A new library file identified as the content, then encoded and published by its source job.
    private async Task<(Guid Item, HlsJobLease Lease, HlsEncodePlan Plan)> EncodeAndPublishAsync(string sha)
    {
        var item = await AddSourceAsync();
        var (lease, plan) = await ClaimAndPlanAsync(sha);
        Assert.Equal(item, lease.Input.ItemId);
        await PublishAsync(lease, plan);
        return (item, lease, plan);
    }

    private Task<bool> ReleaseAsync(HlsJobLease lease)
        => _store.ReleaseAsync(lease.Id, lease.LeaseToken, "ffmpeg failed", MaxAttempts, Lease, _now, TestContext.Current.CancellationToken);

    private Task<Guid?> EnsureMediaJobAsync(string sha, string profile, Guid? preferredItem = null, Func<Guid, bool>? eligible = null)
        => _store.EnsureMediaJobAsync(sha, preferredItem, profile, eligible ?? (_ => true), _now, TestContext.Current.CancellationToken);

    private Task<HlsJob> JobAsync(Guid id) => _environment.JobAsync(id);

    private async Task<HlsJob> SourceJobAsync(Guid item)
    {
        var workKey = "source:" + item.ToString("N", CultureInfo.InvariantCulture);
        await using var context = _environment.CreateDbContext();
        return await context.HlsJobs.AsNoTracking().SingleAsync(j => j.WorkKey == workKey, TestContext.Current.CancellationToken);
    }

    private async Task<List<HlsJobOutput>> OutputsAsync(Guid jobId)
    {
        await using var context = _environment.CreateDbContext();
        return await context.HlsJobOutputs.AsNoTracking().Where(o => o.JobId.Equals(jobId)).OrderBy(o => o.Key).ToListAsync(TestContext.Current.CancellationToken);
    }

    private Task<List<HlsDeletion>> DeletionsAsync() => _environment.DeletionsAsync();
}
