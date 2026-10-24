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
    private const int ClaimPage = 50;
    private const int MaxClaimCandidates = 500;

    private static readonly IReadOnlySet<int> _noQualities = new HashSet<int>();

    // How long the directories of an eager job that stopped are kept for its worker to resume it.
    private static readonly TimeSpan _eagerKeep = TimeSpan.FromDays(1);

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
                    await DiscardOutputsAsync(context, job, now, "The job was cancelled.", cancellationToken).ConfigureAwait(false);
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
    /// Gives a worker the oldest pending job it takes. A job found stale on the way is cancelled and the search goes on, up to 20
    /// such jobs: a source job needs its file in an enabled library, a media job a file of its content in an enabled library, the
    /// preferred item's first, and a rendition it still holds. A job the worker does not take is left waiting for another worker,
    /// up to 500 such jobs: its file is in a library the worker did not ask for, is excluded by the worker, or is on the machine of
    /// another worker.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The lease, or null when no job is available.</returns>
    public Task<HlsJobLease?> ClaimAsync(HlsClaimRequest request, CancellationToken cancellationToken)
        => WriteAsync<HlsJobLease?>(
            async context =>
            {
                // A cancelled job leaves the pending ones, so only the jobs left waiting are skipped.
                var skipped = 0;
                var cancelled = 0;
                while (true)
                {
                    var page = await context.HlsJobs
                        .Where(j => j.Status == (int)HlsJobStatus.Pending)
                        .OrderBy(j => j.DateCreated)
                        .ThenBy(j => j.Id)
                        .Skip(skipped)
                        .Take(ClaimPage)
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (page.Count == 0)
                    {
                        return null;
                    }

                    foreach (var job in page)
                    {
                        var inputs = await FindInputsAsync(context, job, request.IsLibraryEligible, cancellationToken).ConfigureAwait(false);
                        if (inputs.Count == 0)
                        {
                            if (++cancelled > MaxClaimRounds)
                            {
                                return null;
                            }

                            await CancelAsync(context, job, request.Now, cancellationToken).ConfigureAwait(false);
                            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        // The renditions a media job holds decide which workers can take it; a source job does not know them yet.
                        IReadOnlyList<string> keys = [];
                        if (job.Kind == (int)HlsJobKind.Media && request.ExcludedQualities.Count > 0)
                        {
                            var jobId = job.Id;
                            keys = await context.HlsRenditions.Where(r => r.JobId.Equals(jobId)).Select(r => r.Key).ToListAsync(cancellationToken).ConfigureAwait(false);
                        }

                        if (inputs.FirstOrDefault(s => IsAcceptable(request, s) && HlsQualityRules.CanTake(keys, GetExcludedQualities(request, s.LibraryId))) is not { } source)
                        {
                            if (++skipped >= MaxClaimCandidates)
                            {
                                return null;
                            }

                            continue;
                        }

                        return Claim(job, ToRecord(source).File, request);
                    }
                }
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
                        job.Input is null ? null : Deserialize<HlsSourceFile>(job.Input),
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
                    if (!string.Equals(planned.Sha256, sha256, StringComparison.Ordinal))
                    {
                        throw new ArgumentException("The job was already identified as other content.", nameof(request));
                    }

                    return string.Equals(job.Base, request.Base, StringComparison.Ordinal)
                        ? new HlsIdentifyResult(HlsIdentifyOutcome.Planned, planned)
                        : throw new ArgumentException("The job was already identified with another root of the HLS store: " + job.Base, nameof(request));
                }

                // A job a worker started for media it adds has its plan from the start.
                if (job.Input is null)
                {
                    throw new ArgumentException("The job was started for media being added and has no file to identify.", nameof(request));
                }

                // The attempt is not counted: the worker read a file that is no longer the one recorded.
                var input = Deserialize<HlsSourceFile>(job.Input);
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
                    keptStoredProbe = KeepStoredProbe(media, request.Probe, probe);
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

                // A content in no chosen library keeps its renditions, for the orphan rules.
                var qualities = await GetContentQualitiesAsync(context, media, request.GetQualities, cancellationToken).ConfigureAwait(false);
                IReadOnlyList<HlsRenditionSpec> desired = qualities.Count == 0 ? [] : HlsRenditionPlanner.GetDesired(storedProbe, qualities);
                if (qualities.Count > 0)
                {
                    await SyncRenditionsAsync(context, sha256, desired, now, cancellationToken).ConfigureAwait(false);
                }

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
                var holding = await context.HlsRenditions.Where(r => r.Sha256 == sha256 && r.JobId.Equals(jobId)).ToListAsync(cancellationToken).ConfigureAwait(false);

                // The qualities the worker does not encode go back, for a job of another worker once this one ends.
                if (request.ExcludedQualities.Count > 0)
                {
                    if (!HlsQualityRules.CanTake(holding.Select(r => r.Key), request.ExcludedQualities))
                    {
                        await CompleteAsync(context, job, now, cancellationToken).ConfigureAwait(false);
                        return new HlsIdentifyResult(HlsIdentifyOutcome.Done, KeptStoredProbe: keptStoredProbe);
                    }

                    foreach (var rendition in holding.Where(r => HlsQualityRules.IsExcluded(r.Key, request.ExcludedQualities)))
                    {
                        rendition.JobId = null;
                    }
                }

                var held = holding.Where(r => r.JobId.Equals(jobId)).Select(r => r.Key).ToList();
                var specs = desired.Where(s => held.Contains(s.Key)).ToList();
                if (specs.Count == 0)
                {
                    await CompleteAsync(context, job, now, cancellationToken).ConfigureAwait(false);
                    return new HlsIdentifyResult(HlsIdentifyOutcome.Done, KeptStoredProbe: keptStoredProbe);
                }

                var plan = CreatePlan(media, request.Profile, storedProbe, specs, job.LeaseToken!);
                job.Plan = JsonSerializer.Serialize(plan);
                job.Base = request.Base;
                return new HlsIdentifyResult(HlsIdentifyOutcome.Planned, plan, keptStoredProbe);
            },
            cancellationToken);
    }

    /// <summary>
    /// Starts a job for media a worker adds, before its file is in the library: the job holds the renditions of the qualities asked
    /// for, the audio, the text subtitles and the preview, and is leased to the worker at once. The library counts as one of the
    /// content, so its qualities are wanted. A new content is recorded as an orphan of the library, until a library file of it is
    /// found. The renditions left are encoded by the usual jobs later.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What starting the job led to.</returns>
    public Task<HlsEagerJobResult> StartEagerJobAsync(HlsEagerJobRequest request, CancellationToken cancellationToken)
    {
        var sha256 = request.Sha256;
        var now = request.Now;
        return WriteAsync(
            async context =>
            {
                // A content keeps its first probe, as on identification.
                var probe = JsonSerializer.Serialize(request.Probe);
                var media = await context.HlsMedia.FindAsync([sha256], cancellationToken).ConfigureAwait(false);
                var keptStoredProbe = false;
                if (media is null)
                {
                    media = new HlsMedia
                    {
                        Sha256 = sha256,
                        MediaKey = HlsRenditionPaths.GetMediaKey(sha256),
                        LibraryId = request.LibraryId,
                        Probe = probe,
                        DateOrphaned = now,
                        DateCreated = now
                    };
                    context.HlsMedia.Add(media);
                }
                else
                {
                    keptStoredProbe = KeepStoredProbe(media, request.Probe, probe);
                }

                // The library the media is added to counts as if its file were already there.
                var storedProbe = Deserialize<HlsProbe>(media.Probe);
                var qualities = (await GetContentQualitiesAsync(context, media, request.GetQualities, cancellationToken).ConfigureAwait(false))
                    .Union(request.GetQualities(request.LibraryId))
                    .ToList();

                var desired = HlsRenditionPlanner.GetDesired(storedProbe, qualities);
                await SyncRenditionsAsync(context, sha256, desired, now, cancellationToken).ConfigureAwait(false);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                // The worker resumes the job of its earlier addition, or replaces a job nobody works on; it would otherwise encode what
                // another job is encoding, or wait for it.
                var workKey = GetMediaWorkKey(sha256);
                var job = await context.HlsJobs.FirstOrDefaultAsync(j => j.WorkKey == workKey, cancellationToken).ConfigureAwait(false);
                if (job is not null && CanResume(job, request) && await TryResumeAsync(context, job, request, keptStoredProbe, cancellationToken).ConfigureAwait(false) is { } resumed)
                {
                    return resumed;
                }

                if (job is not null && !CanBeCreatedAgain(job))
                {
                    if (!CanBeReplaced(job, now))
                    {
                        return new HlsEagerJobResult(HlsEagerJobOutcome.Busy, KeptStoredProbe: keptStoredProbe);
                    }

                    // A worker whose lease expired may still be uploading until it notices.
                    await ReplaceAsync(context, job, job.Status == (int)HlsJobStatus.Running ? now + request.Lease : now, now, cancellationToken).ConfigureAwait(false);
                }

                await ResetFailedRenditionsAsync(context, sha256, cancellationToken).ConfigureAwait(false);
                if (await context.HlsRenditions.AnyAsync(r => r.Sha256 == sha256 && r.JobId.HasValue, cancellationToken).ConfigureAwait(false))
                {
                    return new HlsEagerJobResult(HlsEagerJobOutcome.Busy, KeptStoredProbe: keptStoredProbe);
                }

                var needed = await context.HlsRenditions.Where(r => r.Sha256 == sha256).Where(NeedsJob(request.Profile)).ToListAsync(cancellationToken).ConfigureAwait(false);
                var specs = desired
                    .Where(s => needed.Any(r => r.Key == s.Key) && (s.Kind != HlsRenditionKind.Video || request.Qualities.Contains(s.Video!.Name)))
                    .ToList();
                if (specs.Count == 0)
                {
                    return new HlsEagerJobResult(HlsEagerJobOutcome.Done, KeptStoredProbe: keptStoredProbe);
                }

                if (job is not null)
                {
                    // The new row has the same work key.
                    context.HlsJobs.Remove(job);
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }

                // The token also names the directories of this attempt.
                var token = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                var plan = CreatePlan(media, request.Profile, storedProbe, specs, token);
                job = NewJob(HlsJobKind.Media, workKey, null, sha256, now);
                job.Status = (int)HlsJobStatus.Running;
                job.Worker = request.Worker;
                job.WorkerId = request.WorkerId;
                job.LeaseToken = token;
                job.LeaseExpires = now + request.Lease;
                job.Attempt = 1;
                job.Plan = JsonSerializer.Serialize(plan);
                job.Base = request.Base;
                context.HlsJobs.Add(job);
                foreach (var rendition in needed.Where(r => specs.Any(s => s.Key == r.Key)))
                {
                    rendition.JobId = job.Id;
                }

                var lease = new HlsJobLease(job.Id, HlsJobKind.Media, null, token, job.LeaseExpires.Value, job.Attempt, plan);
                return new HlsEagerJobResult(HlsEagerJobOutcome.Started, lease, keptStoredProbe);
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
    /// <param name="skippedRoots">The roots whose jobs are left out.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The jobs with their outputs.</returns>
    public Task<IReadOnlyList<HlsVerifyingJob>> GetVerifyingJobsAsync(int limit, IReadOnlyCollection<string> skippedRoots, CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<HlsVerifyingJob>>(
            async context =>
            {
                var jobs = await context.HlsJobs.AsNoTracking()
                    .Where(j => j.Status == (int)HlsJobStatus.Verifying && !skippedRoots.Contains(j.Base ?? string.Empty))
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
                        j.Base ?? string.Empty,
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
    /// <param name="getReplacedGrace">How long a replaced directory is kept, by the root it is in.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What was published, or null when the job is not verifying.</returns>
    public Task<HlsPublishResult?> PublishAsync(Guid jobId, Func<string?, TimeSpan> getReplacedGrace, DateTime now, CancellationToken cancellationToken)
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
                        await QueueDeletionAsync(context, job.Base, output.RelativeDirectory, now, "The rendition is no longer wanted.", cancellationToken).ConfigureAwait(false);
                        context.HlsJobOutputs.Remove(output);
                        continue;
                    }

                    if (rendition.RelativeDirectory is { } replaced && !string.Equals(replaced, output.RelativeDirectory, StringComparison.Ordinal))
                    {
                        await QueueDeletionAsync(context, rendition.Base, replaced, now + getReplacedGrace(rendition.Base), "Replaced by a new output.", cancellationToken).ConfigureAwait(false);
                        await RemoveOutputAsync(context, rendition.PublishedJobId, rendition.Key, cancellationToken).ConfigureAwait(false);
                    }

                    rendition.State = (int)HlsRenditionState.Published;
                    rendition.JobId = null;
                    rendition.PublishedJobId = jobId;
                    rendition.WorkerId = job.WorkerId;
                    rendition.RelativeDirectory = output.RelativeDirectory;
                    rendition.Base = job.Base;
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

                await DiscardOutputsAsync(context, job, now, "The outputs failed verification.", cancellationToken).ConfigureAwait(false);
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

    // The job of an earlier addition of the media by the same worker, stopped or still leased, with its plan and root.
    // Whether the stored probe of a content differs from a new one and is kept. A probe stored before the color space was read
    // takes it from a new probe that is the same otherwise, as it is no other description of the file, only a longer one.
    private static bool KeepStoredProbe(HlsMedia media, HlsProbe probe, string json)
    {
        if (string.Equals(media.Probe, json, StringComparison.Ordinal))
        {
            return false;
        }

        var stored = Deserialize<HlsProbe>(media.Probe);
        if (stored.ColorSpace.Length == 0 && string.Equals(JsonSerializer.Serialize(stored with { ColorSpace = probe.ColorSpace }), json, StringComparison.Ordinal))
        {
            media.Probe = json;
            return false;
        }

        return true;
    }

    private static bool CanResume(HlsJob job, HlsEagerJobRequest request)
        => request.ResumeJobId.Equals(job.Id)
            && request.ResumeToken is not null
            && string.Equals(job.LeaseToken, request.ResumeToken, StringComparison.Ordinal)
            && job.Plan is not null
            && job.Input is null
            && job.WorkerId.Equals(request.WorkerId)
            && string.Equals(job.Base, request.Base, StringComparison.Ordinal)
            && (job.Status == (int)HlsJobStatus.Pending
                || job.Status == (int)HlsJobStatus.Running
                || (job.Status == (int)HlsJobStatus.Cancelled && job.LeaseExpires is null));

    // Leases the job again with its plan, its token and so its directories, which are no longer to be deleted; null when one of its
    // renditions was taken or published meanwhile, or the profile changed.
    private static async Task<HlsEagerJobResult?> TryResumeAsync(JellyfinDbContext context, HlsJob job, HlsEagerJobRequest request, bool keptStoredProbe, CancellationToken cancellationToken)
    {
        var plan = Deserialize<HlsEncodePlan>(job.Plan!);
        if (!string.Equals(plan.Profile, request.Profile, StringComparison.Ordinal))
        {
            return null;
        }

        var sha256 = request.Sha256;
        await ResetFailedRenditionsAsync(context, sha256, cancellationToken).ConfigureAwait(false);
        var keys = plan.Renditions.Select(r => r.Key).ToList();
        var renditions = await context.HlsRenditions.Where(r => r.Sha256 == sha256 && keys.Contains(r.Key)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var jobId = job.Id;
        if (keys.Any(k => !renditions.Any(r => r.Key == k && (r.JobId.Equals(jobId) || (!r.JobId.HasValue && r.State == (int)HlsRenditionState.Missing)))))
        {
            return null;
        }

        foreach (var rendition in renditions)
        {
            rendition.JobId = job.Id;
        }

        var directories = plan.Renditions.Select(r => r.RelativeDirectory).ToList();
        context.HlsDeletions.RemoveRange(await context.HlsDeletions.Where(d => directories.Contains(d.RelativeDirectory)).ToListAsync(cancellationToken).ConfigureAwait(false));
        var now = request.Now;
        job.Status = (int)HlsJobStatus.Running;
        job.Worker = request.Worker;
        job.LeaseExpires = now + request.Lease;
        job.Attempt = Math.Max(1, job.Attempt);
        job.Error = null;
        job.DateModified = now;
        var lease = new HlsJobLease(job.Id, HlsJobKind.Media, null, job.LeaseToken!, job.LeaseExpires.Value, job.Attempt, plan);
        return new HlsEagerJobResult(HlsEagerJobOutcome.Started, lease, keptStoredProbe);
    }

    // A job an addition takes the place of: one waiting, one that failed, or one of another addition whose lease expired.
    private static bool CanBeReplaced(HlsJob job, DateTime now)
        => job.Status == (int)HlsJobStatus.Pending
            || job.Status == (int)HlsJobStatus.Failed
            || (job.Status == (int)HlsJobStatus.Running && job.Input is null && job.LeaseExpires <= now);

    // Cancels the job, releasing its renditions, and queues the directories of its plan that were not published.
    private static async Task ReplaceAsync(JellyfinDbContext context, HlsJob job, DateTime notBefore, DateTime now, CancellationToken cancellationToken)
    {
        if (job.Plan is not null)
        {
            var sha256 = job.Sha256;
            var published = await context.HlsRenditions.Where(r => r.Sha256 == sha256 && r.RelativeDirectory != null).Select(r => r.RelativeDirectory!).ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var rendition in Deserialize<HlsEncodePlan>(job.Plan).Renditions.Where(r => !published.Contains(r.RelativeDirectory)))
            {
                await QueueDeletionAsync(context, job.Base, rendition.RelativeDirectory, notBefore, "The job was replaced by an addition of its media.", cancellationToken).ConfigureAwait(false);
            }
        }

        await CancelAsync(context, job, now, cancellationToken).ConfigureAwait(false);
        job.LeaseToken = null;
        job.LeaseExpires = null;
    }

    // Adding the media again is a reason to try the renditions that failed for good.
    private static async Task ResetFailedRenditionsAsync(JellyfinDbContext context, string sha256, CancellationToken cancellationToken)
    {
        foreach (var rendition in await context.HlsRenditions.Where(r => r.Sha256 == sha256 && r.State == (int)HlsRenditionState.Failed).ToListAsync(cancellationToken).ConfigureAwait(false))
        {
            rendition.State = (int)HlsRenditionState.Missing;
            rendition.JobId = null;
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsLeaseValid([NotNullWhen(true)] HlsJob? job, string leaseToken, DateTime now)
        => job is not null
            && job.Status == (int)HlsJobStatus.Running
            && string.Equals(job.LeaseToken, leaseToken, StringComparison.Ordinal)
            && job.LeaseExpires > now;

    // The plan of the renditions a job encodes, in the directories named by its lease token.
    private static HlsEncodePlan CreatePlan(HlsMedia media, string profile, HlsProbe probe, IEnumerable<HlsRenditionSpec> specs, string token)
        => new(
            media.Sha256,
            media.MediaKey,
            profile,
            probe,
            HlsEncodingProfile.SegmentSeconds,
            HlsEncodingProfile.KeyframeSeconds,
            HlsEncodingProfile.AudioSampleRate,
            HlsEncodingProfile.GetExpectedSegments(probe.DurationSeconds),
            specs.Select(s => new HlsPlannedRendition(s.Key, s.Kind, HlsRenditionPaths.GetRelativeDirectory(media.MediaKey, s, token), s.Video, s.SourceIndex, s.Channels)).ToList());

    // Leases the job to the worker.
    private static HlsJobLease Claim(HlsJob job, HlsSourceFile input, HlsClaimRequest request)
    {
        // The token also names the directories of this attempt.
        var lease = new HlsJobLease(job.Id, (HlsJobKind)job.Kind, input, Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture), request.Now + request.Lease, job.Attempt + 1, null);
        job.Status = (int)HlsJobStatus.Running;
        job.Worker = request.Worker;
        job.WorkerId = request.WorkerId;
        job.LeaseToken = lease.LeaseToken;
        job.LeaseExpires = lease.LeaseExpires;
        job.Attempt = lease.Attempt;
        job.Error = null;
        job.Input = JsonSerializer.Serialize(input);
        job.Plan = null;
        job.Base = null;
        job.DateModified = request.Now;
        return lease;
    }

    // The files the job may read, the preferred first; none when the job is stale.
    private static async Task<List<HlsSource>> FindInputsAsync(JellyfinDbContext context, HlsJob job, Func<Guid, bool> isLibraryEligible, CancellationToken cancellationToken)
    {
        if (job.Kind == (int)HlsJobKind.Source)
        {
            var source = job.ItemId is { } itemId ? await context.HlsSources.FindAsync([itemId], cancellationToken).ConfigureAwait(false) : null;
            return source is not null && isLibraryEligible(source.LibraryId) ? [source] : [];
        }

        var jobId = job.Id;
        if (!await context.HlsRenditions.AnyAsync(r => r.JobId.Equals(jobId), cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var sha256 = job.Sha256;
        var sources = await context.HlsSources.Where(s => s.Sha256 == sha256).ToListAsync(cancellationToken).ConfigureAwait(false);
        return sources.OrderBy(s => !job.ItemId.Equals(s.ItemId)).ThenBy(s => s.ItemId).Where(s => isLibraryEligible(s.LibraryId)).ToList();
    }

    private static IReadOnlySet<int> GetExcludedQualities(HlsClaimRequest request, Guid libraryId)
        => request.ExcludedQualities.TryGetValue(libraryId, out var excluded) ? excluded : _noQualities;

    // Whether the worker takes a file: it is in a library the worker asked for and not excluded by it, and not on another worker.
    private static bool IsAcceptable(HlsClaimRequest request, HlsSource source)
        => (request.Libraries is null || request.Libraries.Contains(source.LibraryId))
            && !request.ExcludedItems.Contains(source.ItemId)
            && (!HlsWorkerLocalAddress.TryParse(source.Path, out var owner, out _) || owner.Equals(request.WorkerId));

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
        job.Base = null;
        job.LeaseToken = null;
        job.LeaseExpires = null;
        job.Attempt = Math.Max(0, job.Attempt - 1);
        job.Error = error;
        job.DateModified = now;
    }

    // Queues the directories of every output the job submitted and forgets those outputs.
    private static async Task DiscardOutputsAsync(JellyfinDbContext context, HlsJob job, DateTime now, string reason, CancellationToken cancellationToken)
    {
        var jobId = job.Id;
        var outputs = await context.HlsJobOutputs.Where(o => o.JobId.Equals(jobId)).OrderBy(o => o.Key).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var output in outputs)
        {
            await QueueDeletionAsync(context, job.Base, output.RelativeDirectory, now, reason, cancellationToken).ConfigureAwait(false);
        }

        context.HlsJobOutputs.RemoveRange(outputs);
    }

    // The worker's directories are queued; a cancelled job only loses its lease, a running one fails. An eager job keeps its plan
    // and directories for its worker to resume; its directories are deleted after a day unless it does.
    private static async Task EndLeaseAsync(JellyfinDbContext context, HlsJob job, string reason, DateTime notBefore, string error, int maxAttempts, DateTime now, CancellationToken cancellationToken)
    {
        var kept = job.Status == (int)HlsJobStatus.Running && job.Input is null && job.Plan is not null;
        if (job.Plan is not null)
        {
            foreach (var rendition in Deserialize<HlsEncodePlan>(job.Plan).Renditions)
            {
                await QueueDeletionAsync(
                    context,
                    job.Base,
                    rendition.RelativeDirectory,
                    kept ? now + _eagerKeep : notBefore,
                    kept ? "The job of media being added stopped." : reason,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (job.Status == (int)HlsJobStatus.Cancelled)
        {
            job.LeaseToken = null;
            job.LeaseExpires = null;
            job.DateModified = now;
        }
        else if (kept)
        {
            Keep(job, error, now);
        }
        else
        {
            await FailAsync(context, job, error, maxAttempts, now, cancellationToken).ConfigureAwait(false);
        }
    }

    // Waits for its worker to resume it, keeping its plan, root and token; a claim cancels it, as no file of it is in a library, or
    // takes it as a new job.
    private static void Keep(HlsJob job, string error, DateTime now)
    {
        job.Status = (int)HlsJobStatus.Pending;
        job.LeaseExpires = null;
        job.Attempt = 0;
        job.Error = error;
        job.DateModified = now;
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
            job.Base = null;
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
