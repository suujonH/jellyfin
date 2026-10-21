using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Model.Hls;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Hls;

/// <content>
/// The jobs: creating, claiming, identifying, verifying, publishing and ending them.
/// </content>
public sealed partial class HlsJobStore
{
    private const int MaxClaimRounds = 20;

    /// <summary>
    /// Creates the job of a content whose renditions need one and lets it hold them. A pending job is reused, a running or verifying
    /// one and a cancelled one whose worker still holds the lease are left alone; any other is created again with a new id, since
    /// the outputs it may have published are kept under its id.
    /// </summary>
    /// <param name="sha256">The hash of the content.</param>
    /// <param name="preferredItem">The item whose file the job should read.</param>
    /// <param name="profile">The current encoding profile.</param>
    /// <param name="isLibraryEligible">Whether pre-encoded HLS is enabled for a library.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The job, or null when nothing needs one or no file of the content is in an enabled library.</returns>
    public Task<Guid?> EnsureMediaJobAsync(string sha256, Guid? preferredItem, string profile, Func<Guid, bool> isLibraryEligible, DateTime now, CancellationToken cancellationToken)
        => WriteAsync<Guid?>(
            async context =>
            {
                var needed = await context.HlsRenditions.Where(r => r.Sha256 == sha256).Where(NeedsJob(profile)).ToListAsync(cancellationToken).ConfigureAwait(false);
                if (needed.Count == 0)
                {
                    return null;
                }

                // Claiming would cancel the job at once, and the next schedule would create it again.
                var libraries = await context.HlsSources.Where(s => s.Sha256 == sha256).Select(s => s.LibraryId).ToListAsync(cancellationToken).ConfigureAwait(false);
                if (!libraries.Any(isLibraryEligible))
                {
                    return null;
                }

                var workKey = GetMediaWorkKey(sha256);
                var job = await context.HlsJobs.FirstOrDefaultAsync(j => j.WorkKey == workKey, cancellationToken).ConfigureAwait(false);
                if (job?.Status == (int)HlsJobStatus.Pending)
                {
                    job.DateModified = now;
                }
                else if (job is not null && !CanBeCreatedAgain(job) && job.Status != (int)HlsJobStatus.Failed)
                {
                    return null;
                }
                else
                {
                    if (job is not null)
                    {
                        // The new row has the same work key.
                        context.HlsJobs.Remove(job);
                        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }

                    job = NewJob(HlsJobKind.Media, workKey, preferredItem, sha256, now);
                    context.HlsJobs.Add(job);
                }

                foreach (var rendition in needed)
                {
                    rendition.JobId = job.Id;
                }

                return job.Id;
            },
            cancellationToken);

    /// <summary>
    /// Cancels a job that has not finished. The outputs a verifying job submitted are queued for deletion here, since it has no
    /// lease left; a running job keeps its lease, so its worker is refused on the next call and its directories are queued when the
    /// lease is released or expires.
    /// </summary>
    /// <param name="id">The job.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the job was cancelled.</returns>
    public Task<bool> CancelJobAsync(Guid id, DateTime now, CancellationToken cancellationToken)
        => WriteAsync<bool>(
            async context =>
            {
                var job = await context.HlsJobs.FindAsync([id], cancellationToken).ConfigureAwait(false);
                if (job?.Status is not ((int)HlsJobStatus.Pending or (int)HlsJobStatus.Running or (int)HlsJobStatus.Verifying))
                {
                    return false;
                }

                if (job.Status == (int)HlsJobStatus.Verifying)
                {
                    await DiscardOutputsAsync(context, job.Id, now, "The job was cancelled.", cancellationToken).ConfigureAwait(false);
                }

                await CancelAsync(context, job, now, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Cancels every pending and running job and releases their renditions; verifying jobs finish.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task CancelAllJobsAsync(DateTime now, CancellationToken cancellationToken)
        => WriteAsync(
            async context =>
            {
                await context.HlsRenditions
                    .Where(r => context.HlsJobs.Any(j => r.JobId.Equals(j.Id) && (j.Status == (int)HlsJobStatus.Pending || j.Status == (int)HlsJobStatus.Running)))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.JobId, (Guid?)null), cancellationToken)
                    .ConfigureAwait(false);
                await context.HlsJobs
                    .Where(j => j.Status == (int)HlsJobStatus.Pending || j.Status == (int)HlsJobStatus.Running)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, (int)HlsJobStatus.Cancelled).SetProperty(j => j.DateModified, now), cancellationToken)
                    .ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Gives a worker the oldest pending job. A job found stale on the way is cancelled and the search goes on, up to 20 jobs:
    /// a source job needs its file in an enabled library, a media job a file of its content in an enabled library, the preferred
    /// item's first, and a rendition it still holds.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The lease, or null when no job is available.</returns>
    public Task<HlsJobLease?> ClaimAsync(HlsClaimRequest request, CancellationToken cancellationToken)
        => WriteAsync<HlsJobLease?>(
            async context =>
            {
                for (var round = 0; round < MaxClaimRounds; round++)
                {
                    var job = await context.HlsJobs
                        .Where(j => j.Status == (int)HlsJobStatus.Pending)
                        .OrderBy(j => j.DateCreated)
                        .ThenBy(j => j.Id)
                        .FirstOrDefaultAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (job is null)
                    {
                        return null;
                    }

                    var input = await FindInputAsync(context, job, request.IsLibraryEligible, cancellationToken).ConfigureAwait(false);
                    if (input is null)
                    {
                        await CancelAsync(context, job, request.Now, cancellationToken).ConfigureAwait(false);
                        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    // The token also names the directories of this attempt.
                    var lease = new HlsJobLease(job.Id, (HlsJobKind)job.Kind, input, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), request.Now + request.Lease, job.Attempt + 1, null);
                    job.Status = (int)HlsJobStatus.Running;
                    job.Worker = request.Worker;
                    job.LeaseToken = lease.LeaseToken;
                    job.LeaseExpires = lease.LeaseExpires;
                    job.Attempt = lease.Attempt;
                    job.Error = null;
                    job.Input = JsonSerializer.Serialize(input);
                    job.Plan = null;
                    job.DateModified = request.Now;
                    return lease;
                }

                return null;
            },
            cancellationToken);

    /// <summary>
    /// Extends a valid lease.
    /// </summary>
    /// <param name="id">The job.</param>
    /// <param name="leaseToken">The token of the lease.</param>
    /// <param name="lease">How long the lease lasts from now.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the lease was valid.</returns>
    public Task<bool> HeartbeatAsync(Guid id, string leaseToken, TimeSpan lease, DateTime now, CancellationToken cancellationToken)
        => WriteAsync<bool>(
            async context =>
            {
                var job = await context.HlsJobs.FindAsync([id], cancellationToken).ConfigureAwait(false);
                if (!IsLeaseValid(job, leaseToken, now))
                {
                    return false;
                }

                job.LeaseExpires = now + lease;
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Gets a job a worker holds a valid lease of.
    /// </summary>
    /// <param name="id">The job.</param>
    /// <param name="leaseToken">The token of the lease.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The lease, or null.</returns>
    public Task<HlsJobLease?> GetLeaseAsync(Guid id, string leaseToken, DateTime now, CancellationToken cancellationToken)
        => ReadAsync(
            async context =>
            {
                var job = await context.HlsJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id.Equals(id), cancellationToken).ConfigureAwait(false);
                return IsLeaseValid(job, leaseToken, now)
                    ? new HlsJobLease(
                        job.Id,
                        (HlsJobKind)job.Kind,
                        Deserialize<HlsSourceFile>(job.Input!),
                        leaseToken,
                        job.LeaseExpires.GetValueOrDefault(),
                        job.Attempt,
                        job.Plan is null ? null : Deserialize<HlsEncodePlan>(job.Plan))
                    : null;
            },
            cancellationToken);

    /// <summary>
    /// Records what the file of a job is and decides what the job encodes: the renditions of the content that need a job, which the
    /// job takes over. A job left with nothing to encode is complete, so the same content is encoded once.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What identifying led to.</returns>
    /// <exception cref="ArgumentException">The hash is not valid, or the job was already identified as other content.</exception>
    public Task<HlsIdentifyResult> IdentifyAsync(HlsIdentifyRequest request, CancellationToken cancellationToken)
    {
        var sha256 = HlsProbeReader.NormalizeSha256(request.Sha256);
        var now = request.Now;
        return WriteAsync(
            async context =>
            {
                var job = await context.HlsJobs.FindAsync([request.JobId], cancellationToken).ConfigureAwait(false);
                if (!IsLeaseValid(job, request.LeaseToken, now))
                {
                    return new HlsIdentifyResult(HlsIdentifyOutcome.LeaseLost);
                }

                // A worker that retries gets the plan it was given.
                if (job.Plan is not null)
                {
                    var planned = Deserialize<HlsEncodePlan>(job.Plan);
                    return string.Equals(planned.Sha256, sha256, StringComparison.Ordinal)
                        ? new HlsIdentifyResult(HlsIdentifyOutcome.Planned, planned)
                        : throw new ArgumentException("The job was already identified as other content.", nameof(request));
                }

                // The attempt is not counted: the worker read a file that is no longer the one recorded.
                var input = Deserialize<HlsSourceFile>(job.Input!);
                var source = await context.HlsSources.FindAsync([input.ItemId], cancellationToken).ConfigureAwait(false);
                if (source is null && job.Kind == (int)HlsJobKind.Source)
                {
                    await CancelAsync(context, job, now, cancellationToken).ConfigureAwait(false);
                    return new HlsIdentifyResult(HlsIdentifyOutcome.SourceChanged);
                }

                // A media job takes another file of its content on the next claim, or is cancelled when none is left.
                if (source is null || !IsSameFile(source, input))
                {
                    Requeue(job, "The file changed after the job was claimed.", now);
                    return new HlsIdentifyResult(HlsIdentifyOutcome.SourceChanged);
                }

                if (job.Kind == (int)HlsJobKind.Media && !string.Equals(job.Sha256, sha256, StringComparison.Ordinal))
                {
                    source.Sha256 = null;
                    Requeue(job, "The file is no longer the content of the job.", now);
                    await EnsureSourceJobsAsync(context, [source.ItemId], now, cancellationToken).ConfigureAwait(false);
                    return new HlsIdentifyResult(HlsIdentifyOutcome.SourceChanged);
                }

                // A content keeps its first probe: another ffprobe build may describe the same file differently.
                var probe = JsonSerializer.Serialize(request.Probe);
                var media = await context.HlsMedia.FindAsync([sha256], cancellationToken).ConfigureAwait(false);
                var keptStoredProbe = false;
                if (media is null)
                {
                    media = new HlsMedia { Sha256 = sha256, MediaKey = HlsRenditionPaths.GetMediaKey(sha256), LibraryId = source.LibraryId, Probe = probe, DateCreated = now };
                    context.HlsMedia.Add(media);
                }
                else
                {
                    keptStoredProbe = !string.Equals(media.Probe, probe, StringComparison.Ordinal);
                    media.DateOrphaned = null;
                }

                // A source job whose file became other content between attempts gives back what it held of the old one; otherwise
                // that content would wait for this job, and fail with it.
                if (job.Sha256 is not null && !string.Equals(job.Sha256, sha256, StringComparison.Ordinal))
                {
                    await ReleaseRenditionsAsync(context, job.Id, cancellationToken).ConfigureAwait(false);
                }

                source.Sha256 = sha256;
                job.Sha256 = sha256;
                job.DateModified = now;
                var storedProbe = Deserialize<HlsProbe>(media.Probe);
                var desired = HlsRenditionPlanner.GetDesired(storedProbe, request.Qualities);
                await SyncRenditionsAsync(context, sha256, desired, now, cancellationToken).ConfigureAwait(false);
                if (!request.IsLibraryEligible(source.LibraryId))
                {
                    await CompleteAsync(context, job, now, cancellationToken).ConfigureAwait(false);
                    return new HlsIdentifyResult(HlsIdentifyOutcome.Done, KeptStoredProbe: keptStoredProbe);
                }

                // Renditions another job holds stay with it.
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                var needed = await context.HlsRenditions.Where(r => r.Sha256 == sha256).Where(NeedsJob(request.Profile)).ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var rendition in needed)
                {
                    rendition.JobId = job.Id;
                }

                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                var jobId = job.Id;
                var held = await context.HlsRenditions.Where(r => r.Sha256 == sha256 && r.JobId.Equals(jobId)).Select(r => r.Key).ToListAsync(cancellationToken).ConfigureAwait(false);
                var specs = desired.Where(s => held.Contains(s.Key)).ToList();
                if (specs.Count == 0)
                {
                    await CompleteAsync(context, job, now, cancellationToken).ConfigureAwait(false);
                    return new HlsIdentifyResult(HlsIdentifyOutcome.Done, KeptStoredProbe: keptStoredProbe);
                }

                var token = job.LeaseToken!;
                var plan = new HlsEncodePlan(
                    sha256,
                    media.MediaKey,
                    request.Profile,
                    storedProbe,
                    HlsEncodingProfile.SegmentSeconds,
                    HlsEncodingProfile.KeyframeSeconds,
                    HlsEncodingProfile.AudioSampleRate,
                    HlsEncodingProfile.GetExpectedSegments(storedProbe.DurationSeconds),
                    specs.Select(s => new HlsPlannedRendition(s.Key, s.Kind, HlsRenditionPaths.GetRelativeDirectory(media.MediaKey, s, token), s.Video, s.SourceIndex, s.Channels)).ToList());
                job.Plan = JsonSerializer.Serialize(plan);
                return new HlsIdentifyResult(HlsIdentifyOutcome.Planned, plan, keptStoredProbe);
            },
            cancellationToken);
    }

    /// <summary>
    /// Records the outputs of a job and ends its lease; they wait to be verified. The outputs are checked against the plan here,
    /// and their directories always come from the plan.
    /// </summary>
    /// <param name="id">The job.</param>
    /// <param name="leaseToken">The token of the lease.</param>
    /// <param name="outputs">The renditions the worker uploaded.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the lease was valid and the job has a plan.</returns>
    /// <exception cref="ArgumentException">The outputs do not agree with the plan.</exception>
    public Task<bool> SubmitOutputsAsync(Guid id, string leaseToken, IReadOnlyList<HlsRenditionOutput> outputs, DateTime now, CancellationToken cancellationToken)
        => WriteAsync<bool>(
            async context =>
            {
                var job = await context.HlsJobs.FindAsync([id], cancellationToken).ConfigureAwait(false);
                if (!IsLeaseValid(job, leaseToken, now) || job.Plan is null)
                {
                    return false;
                }

                var plan = Deserialize<HlsEncodePlan>(job.Plan);
                HlsCompletionValidator.Validate(plan, outputs);
                job.Status = (int)HlsJobStatus.Verifying;
                job.LeaseExpires = null;
                job.DateModified = now;
                foreach (var output in outputs)
                {
                    context.HlsJobOutputs.Add(new HlsJobOutput
                    {
                        JobId = job.Id,
                        Key = output.Key,
                        RelativeDirectory = plan.Renditions.First(r => r.Key == output.Key).RelativeDirectory,
                        Summary = JsonSerializer.Serialize(output.Summary),
                        Files = JsonSerializer.Serialize(output.Files),
                        Segments = JsonSerializer.Serialize(output.SegmentDurations)
                    });
                }

                return true;
            },
            cancellationToken);

    /// <summary>
    /// Gets the jobs whose outputs wait to be verified, the oldest first.
    /// </summary>
    /// <param name="limit">The most jobs to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The jobs with their outputs.</returns>
    public Task<IReadOnlyList<HlsVerifyingJob>> GetVerifyingJobsAsync(int limit, CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<HlsVerifyingJob>>(
            async context =>
            {
                var jobs = await context.HlsJobs.AsNoTracking()
                    .Where(j => j.Status == (int)HlsJobStatus.Verifying)
                    .OrderBy(j => j.DateModified)
                    .ThenBy(j => j.Id)
                    .Take(limit)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                var ids = jobs.Select(j => j.Id).ToList();
                var outputs = await context.HlsJobOutputs.AsNoTracking().Where(o => ids.Contains(o.JobId)).OrderBy(o => o.Key).ToListAsync(cancellationToken).ConfigureAwait(false);
                return jobs
                    .Select(j => new HlsVerifyingJob(
                        j.Id,
                        j.Sha256!,
                        j.Attempt,
                        outputs.Where(o => o.JobId.Equals(j.Id)).Select(o => new HlsVerifyingOutput(o.Key, o.RelativeDirectory, Deserialize<List<HlsOutputFile>>(o.Files))).ToList()))
                    .ToList();
            },
            cancellationToken);

    /// <summary>
    /// Publishes the verified outputs of a job and completes it. A rendition the job no longer holds is not published and its output
    /// is queued for deletion at once; a directory a publication replaces is deleted only after the grace, so a playback that already
    /// has its playlists keeps working.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="replacedGrace">How long a replaced directory is kept.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was published, or null when the job is not verifying.</returns>
    public Task<HlsPublishResult?> PublishAsync(Guid jobId, TimeSpan replacedGrace, DateTime now, CancellationToken cancellationToken)
        => WriteAsync<HlsPublishResult?>(
            async context =>
            {
                var job = await context.HlsJobs.FindAsync([jobId], cancellationToken).ConfigureAwait(false);
                if (job is not { Status: (int)HlsJobStatus.Verifying, Sha256: { } sha256, Plan: { } planJson })
                {
                    return null;
                }

                var profile = Deserialize<HlsEncodePlan>(planJson).Profile;
                var outputs = await context.HlsJobOutputs.Where(o => o.JobId.Equals(jobId)).OrderBy(o => o.Key).ToListAsync(cancellationToken).ConfigureAwait(false);
                var published = new List<string>();
                foreach (var output in outputs)
                {
                    var rendition = await context.HlsRenditions.FindAsync([sha256, output.Key], cancellationToken).ConfigureAwait(false);
                    if (rendition is null || !rendition.JobId.Equals(jobId))
                    {
                        await QueueDeletionAsync(context, output.RelativeDirectory, now, "The rendition is no longer wanted.", cancellationToken).ConfigureAwait(false);
                        context.HlsJobOutputs.Remove(output);
                        continue;
                    }

                    if (rendition.RelativeDirectory is { } replaced && !string.Equals(replaced, output.RelativeDirectory, StringComparison.Ordinal))
                    {
                        await QueueDeletionAsync(context, replaced, now + replacedGrace, "Replaced by a new output.", cancellationToken).ConfigureAwait(false);
                        await RemoveOutputAsync(context, rendition.PublishedJobId, rendition.Key, cancellationToken).ConfigureAwait(false);
                    }

                    rendition.State = (int)HlsRenditionState.Published;
                    rendition.JobId = null;
                    rendition.PublishedJobId = jobId;
                    rendition.RelativeDirectory = output.RelativeDirectory;
                    rendition.Summary = output.Summary;
                    rendition.Profile = profile;
                    rendition.DatePublished = now;
                    published.Add(output.Key);
                }

                await CompleteAsync(context, job, now, cancellationToken).ConfigureAwait(false);
                return new HlsPublishResult(sha256, published);
            },
            cancellationToken);

    /// <summary>
    /// Fails a verifying job: its outputs are queued for deletion and it is retried, or fails for good after the last attempt.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="error">Why verifying failed.</param>
    /// <param name="maxAttempts">How often a job is claimed at most.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the job was verifying.</returns>
    public Task<bool> FailVerificationAsync(Guid jobId, string error, int maxAttempts, DateTime now, CancellationToken cancellationToken)
        => WriteAsync<bool>(
            async context =>
            {
                var job = await context.HlsJobs.FindAsync([jobId], cancellationToken).ConfigureAwait(false);
                if (job?.Status != (int)HlsJobStatus.Verifying)
                {
                    return false;
                }

                await DiscardOutputsAsync(context, jobId, now, "The outputs failed verification.", cancellationToken).ConfigureAwait(false);
                await FailAsync(context, job, error, maxAttempts, now, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Ends a lease the worker gives up: the directories of its plan are queued for deletion after the grace, as uploads under way
    /// may still land in them, and the job is retried, or fails for good after the last attempt. A cancelled job only loses its lease.
    /// </summary>
    /// <param name="id">The job.</param>
    /// <param name="leaseToken">The token of the lease.</param>
    /// <param name="error">What the worker reported.</param>
    /// <param name="maxAttempts">How often a job is claimed at most.</param>
    /// <param name="grace">How long the directories are kept.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the worker held the lease.</returns>
    public Task<bool> ReleaseAsync(Guid id, string leaseToken, string error, int maxAttempts, TimeSpan grace, DateTime now, CancellationToken cancellationToken)
        => WriteAsync<bool>(
            async context =>
            {
                var job = await context.HlsJobs.FindAsync([id], cancellationToken).ConfigureAwait(false);
                if (job?.Status is not ((int)HlsJobStatus.Running or (int)HlsJobStatus.Cancelled) || !string.Equals(job.LeaseToken, leaseToken, StringComparison.Ordinal))
                {
                    return false;
                }

                await EndLeaseAsync(context, job, "The worker reported a failure.", now + grace, error, maxAttempts, now, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Ends the leases that expired, as <see cref="ReleaseAsync"/> does. A worker that has not noticed the expiry may still be
    /// uploading, so the directories are deleted only after the grace.
    /// </summary>
    /// <param name="maxAttempts">How often a job is claimed at most.</param>
    /// <param name="grace">How long the directories are kept.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>How many leases expired.</returns>
    public Task<int> ReapExpiredLeasesAsync(int maxAttempts, TimeSpan grace, DateTime now, CancellationToken cancellationToken)
        => WriteAsync<int>(
            async context =>
            {
                var expired = await context.HlsJobs
                    .Where(j => (j.Status == (int)HlsJobStatus.Running || j.Status == (int)HlsJobStatus.Cancelled) && j.LeaseExpires <= now)
                    .OrderBy(j => j.LeaseExpires)
                    .ThenBy(j => j.Id)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var job in expired)
                {
                    await EndLeaseAsync(context, job, "The lease expired.", now + grace, "The lease expired.", maxAttempts, now, cancellationToken).ConfigureAwait(false);
                }

                return expired.Count;
            },
            cancellationToken);

    /// <summary>
    /// Prepares a rebuild: pending jobs are cancelled, failed renditions are missing again, renditions are released unless a running
    /// or verifying job holds them, and failed source jobs are cancelled, so scheduling creates every job again.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task ResetForRebuildAsync(DateTime now, CancellationToken cancellationToken)
        => WriteAsync(
            async context =>
            {
                await context.HlsRenditions
                    .Where(r => context.HlsJobs.Any(j => r.JobId.Equals(j.Id) && j.Status == (int)HlsJobStatus.Pending))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.JobId, (Guid?)null), cancellationToken)
                    .ConfigureAwait(false);
                await context.HlsJobs
                    .Where(j => j.Status == (int)HlsJobStatus.Pending)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, (int)HlsJobStatus.Cancelled).SetProperty(j => j.DateModified, now), cancellationToken)
                    .ConfigureAwait(false);
                await context.HlsRenditions
                    .Where(r => r.State == (int)HlsRenditionState.Failed)
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.State, (int)HlsRenditionState.Missing).SetProperty(r => r.JobId, (Guid?)null), cancellationToken)
                    .ConfigureAwait(false);
                await context.HlsRenditions
                    .Where(r => r.JobId.HasValue
                        && !context.HlsJobs.Any(j => r.JobId.Equals(j.Id) && (j.Status == (int)HlsJobStatus.Running || j.Status == (int)HlsJobStatus.Verifying)))
                    .ExecuteUpdateAsync(s => s.SetProperty(r => r.JobId, (Guid?)null), cancellationToken)
                    .ConfigureAwait(false);
                await context.HlsJobs
                    .Where(j => j.Status == (int)HlsJobStatus.Failed && j.Kind == (int)HlsJobKind.Source)
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, (int)HlsJobStatus.Cancelled).SetProperty(j => j.DateModified, now), cancellationToken)
                    .ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Gets the most recently changed jobs.
    /// </summary>
    /// <param name="limit">The most jobs to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The jobs.</returns>
    public Task<IReadOnlyList<HlsJobSummary>> ListJobsAsync(int limit, CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<HlsJobSummary>>(
            async context => await context.HlsJobs.AsNoTracking()
                .OrderByDescending(j => j.DateModified)
                .ThenBy(j => j.Id)
                .Take(limit)
                .Select(j => new HlsJobSummary(j.Id, (HlsJobKind)j.Kind, j.ItemId, j.Sha256, (HlsJobStatus)j.Status, j.Attempt, j.Worker, j.Error, j.DateModified))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),
            cancellationToken);

    /// <summary>
    /// Counts the jobs by status, the queued deletions, the contents and the published renditions.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The counts.</returns>
    public Task<HlsJobCounts> GetCountsAsync(CancellationToken cancellationToken)
        => ReadAsync(
            async context =>
            {
                var jobs = await context.HlsJobs.GroupBy(j => j.Status).Select(g => new { Status = g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Status, g => g.Count, cancellationToken).ConfigureAwait(false);
                int Jobs(HlsJobStatus status) => jobs.GetValueOrDefault((int)status);
                return new HlsJobCounts(
                    Jobs(HlsJobStatus.Pending),
                    Jobs(HlsJobStatus.Running),
                    Jobs(HlsJobStatus.Verifying),
                    Jobs(HlsJobStatus.Complete),
                    Jobs(HlsJobStatus.Failed),
                    Jobs(HlsJobStatus.Cancelled),
                    await context.HlsDeletions.CountAsync(cancellationToken).ConfigureAwait(false),
                    await context.HlsMedia.CountAsync(cancellationToken).ConfigureAwait(false),
                    await context.HlsRenditions.CountAsync(r => r.State == (int)HlsRenditionState.Published, cancellationToken).ConfigureAwait(false));
            },
            cancellationToken);

    private static bool IsLeaseValid([NotNullWhen(true)] HlsJob? job, string leaseToken, DateTime now)
        => job is not null
            && job.Status == (int)HlsJobStatus.Running
            && string.Equals(job.LeaseToken, leaseToken, StringComparison.Ordinal)
            && job.LeaseExpires > now;

    // The file the job reads, or null when the job is stale.
    private static async Task<HlsSourceFile?> FindInputAsync(JellyfinDbContext context, HlsJob job, Func<Guid, bool> isLibraryEligible, CancellationToken cancellationToken)
    {
        if (job.Kind == (int)HlsJobKind.Source)
        {
            var source = job.ItemId is { } itemId ? await context.HlsSources.FindAsync([itemId], cancellationToken).ConfigureAwait(false) : null;
            return source is not null && isLibraryEligible(source.LibraryId) ? ToRecord(source).File : null;
        }

        var jobId = job.Id;
        if (!await context.HlsRenditions.AnyAsync(r => r.JobId.Equals(jobId), cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var sha256 = job.Sha256;
        var sources = await context.HlsSources.Where(s => s.Sha256 == sha256).ToListAsync(cancellationToken).ConfigureAwait(false);
        var input = sources.OrderBy(s => !job.ItemId.Equals(s.ItemId)).ThenBy(s => s.ItemId).FirstOrDefault(s => isLibraryEligible(s.LibraryId));
        return input is null ? null : ToRecord(input).File;
    }

    // Cancels the job and releases its renditions. The lease is kept: its worker is refused on the next call, and releasing the
    // lease or its expiry still queues the directories of the worker.
    private static async Task CancelAsync(JellyfinDbContext context, HlsJob job, DateTime now, CancellationToken cancellationToken)
    {
        job.Status = (int)HlsJobStatus.Cancelled;
        job.DateModified = now;
        await ReleaseRenditionsAsync(context, job.Id, cancellationToken).ConfigureAwait(false);
    }

    // Completes the job and releases the renditions it still holds.
    private static async Task CompleteAsync(JellyfinDbContext context, HlsJob job, DateTime now, CancellationToken cancellationToken)
    {
        job.Status = (int)HlsJobStatus.Complete;
        job.DateModified = now;
        await ReleaseRenditionsAsync(context, job.Id, cancellationToken).ConfigureAwait(false);
    }

    private static async Task ReleaseRenditionsAsync(JellyfinDbContext context, Guid jobId, CancellationToken cancellationToken)
    {
        // The rows are read from the database, so earlier changes go first.
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var rendition in await context.HlsRenditions.Where(r => r.JobId.Equals(jobId)).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            rendition.JobId = null;
        }
    }

    // Waits again for a claim without counting the attempt; the job keeps its renditions.
    private static void Requeue(HlsJob job, string error, DateTime now)
    {
        job.Status = (int)HlsJobStatus.Pending;
        job.Input = null;
        job.Plan = null;
        job.LeaseToken = null;
        job.LeaseExpires = null;
        job.Attempt = Math.Max(0, job.Attempt - 1);
        job.Error = error;
        job.DateModified = now;
    }

    // Queues the directories of every output the job submitted and forgets those outputs.
    private static async Task DiscardOutputsAsync(JellyfinDbContext context, Guid jobId, DateTime now, string reason, CancellationToken cancellationToken)
    {
        var outputs = await context.HlsJobOutputs.Where(o => o.JobId.Equals(jobId)).OrderBy(o => o.Key).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var output in outputs)
        {
            await QueueDeletionAsync(context, output.RelativeDirectory, now, reason, cancellationToken).ConfigureAwait(false);
        }

        context.HlsJobOutputs.RemoveRange(outputs);
    }

    // The worker's directories are queued; a cancelled job only loses its lease, a running one fails.
    private static async Task EndLeaseAsync(JellyfinDbContext context, HlsJob job, string reason, DateTime notBefore, string error, int maxAttempts, DateTime now, CancellationToken cancellationToken)
    {
        if (job.Plan is not null)
        {
            foreach (var rendition in Deserialize<HlsEncodePlan>(job.Plan).Renditions)
            {
                await QueueDeletionAsync(context, rendition.RelativeDirectory, notBefore, reason, cancellationToken).ConfigureAwait(false);
            }
        }

        if (job.Status == (int)HlsJobStatus.Cancelled)
        {
            job.LeaseToken = null;
            job.LeaseExpires = null;
            job.DateModified = now;
        }
        else
        {
            await FailAsync(context, job, error, maxAttempts, now, cancellationToken).ConfigureAwait(false);
        }
    }

    // After the last attempt the job fails for good: its missing renditions fail and are released, its published ones stay held so
    // they are not scheduled again until a rebuild. Otherwise it waits again, keeping its renditions, and identifies its file anew.
    private static async Task FailAsync(JellyfinDbContext context, HlsJob job, string error, int maxAttempts, DateTime now, CancellationToken cancellationToken)
    {
        job.Error = error;
        job.LeaseToken = null;
        job.LeaseExpires = null;
        job.DateModified = now;
        if (job.Attempt < maxAttempts)
        {
            job.Status = (int)HlsJobStatus.Pending;
            job.Input = null;
            job.Plan = null;
            return;
        }

        job.Status = (int)HlsJobStatus.Failed;
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var jobId = job.Id;
        foreach (var rendition in await context.HlsRenditions.Where(r => r.JobId.Equals(jobId) && r.State == (int)HlsRenditionState.Missing).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            rendition.State = (int)HlsRenditionState.Failed;
            rendition.JobId = null;
        }
    }
}
