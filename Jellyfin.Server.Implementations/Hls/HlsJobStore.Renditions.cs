using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Model.Hls;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Hls;

/// <content>
/// The renditions of the contents.
/// </content>
public sealed partial class HlsJobStore
{
    /// <summary>
    /// Brings the renditions of a content in line with its stored probe and the qualities: renditions no longer wanted are removed
    /// and their directories queued for deletion, new ones are added as missing.
    /// </summary>
    /// <param name="sha256">The hash of the content.</param>
    /// <param name="qualities">The qualities chosen in the settings.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether any rendition was added or removed.</returns>
    public Task<bool> SyncRenditionsAsync(string sha256, IReadOnlyCollection<int> qualities, DateTime now, CancellationToken cancellationToken)
        => WriteAsync<bool>(
            async context =>
            {
                var media = await context.HlsMedia.FindAsync([sha256], cancellationToken).ConfigureAwait(false);
                return media is not null
                    && await SyncRenditionsAsync(context, sha256, HlsRenditionPlanner.GetDesired(Deserialize<HlsProbe>(media.Probe), qualities), now, cancellationToken).ConfigureAwait(false);
            },
            cancellationToken);

    /// <summary>
    /// Gets the renditions of a content.
    /// </summary>
    /// <param name="sha256">The hash of the content.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The renditions, by key.</returns>
    public Task<IReadOnlyList<HlsRenditionRecord>> GetRenditionsAsync(string sha256, CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<HlsRenditionRecord>>(
            async context => (await context.HlsRenditions.AsNoTracking().Where(r => r.Sha256 == sha256).OrderBy(r => r.Key).ToListAsync(cancellationToken).ConfigureAwait(false))
                .Select(ToRecord)
                .ToList(),
            cancellationToken);

    /// <summary>
    /// Gets every published rendition.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The renditions, by content and key.</returns>
    public Task<IReadOnlyList<HlsRenditionRecord>> GetPublishedRenditionsAsync(CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<HlsRenditionRecord>>(
            async context => (await context.HlsRenditions.AsNoTracking()
                    .Where(r => r.State == (int)HlsRenditionState.Published)
                    .OrderBy(r => r.Sha256)
                    .ThenBy(r => r.Key)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .Select(ToRecord)
                .ToList(),
            cancellationToken);

    /// <summary>
    /// Gets the contents with a rendition that needs a job.
    /// </summary>
    /// <param name="profile">The current encoding profile.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The hashes, in order.</returns>
    public Task<IReadOnlyList<string>> GetShasNeedingJobsAsync(string profile, CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<string>>(
            async context => await context.HlsRenditions.AsNoTracking().Where(NeedsJob(profile)).Select(r => r.Sha256).Distinct().OrderBy(s => s).ToListAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken);

    /// <summary>
    /// Gets the contents with a published rendition, which a full verification goes through one at a time.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The hashes, in order.</returns>
    public Task<IReadOnlyList<string>> GetPublishedShasAsync(CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<string>>(
            async context => await context.HlsRenditions.AsNoTracking()
                .Where(r => r.State == (int)HlsRenditionState.Published)
                .Select(r => r.Sha256)
                .Distinct()
                .OrderBy(s => s)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),
            cancellationToken);

    /// <summary>
    /// Gets the most recently published directories, which a store root that has no marker yet must still contain.
    /// </summary>
    /// <param name="limit">The most directories to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The directories.</returns>
    public Task<IReadOnlyList<string>> GetPublishedSampleAsync(int limit, CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<string>>(
            async context => await context.HlsRenditions.AsNoTracking()
                .Where(r => r.State == (int)HlsRenditionState.Published && r.RelativeDirectory != null)
                .OrderByDescending(r => r.DatePublished)
                .ThenBy(r => r.RelativeDirectory)
                .Take(limit)
                .Select(r => r.RelativeDirectory!)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),
            cancellationToken);

    /// <summary>
    /// Gets the published renditions with the files the job that published them reported.
    /// </summary>
    /// <param name="sha256">The hash of a content, or null for every content.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The manifests, by content and key.</returns>
    public Task<IReadOnlyList<HlsPublishedManifest>> GetPublishedManifestsAsync(string? sha256, CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<HlsPublishedManifest>>(
            async context => (await context.HlsRenditions.AsNoTracking()
                    .Where(r => r.State == (int)HlsRenditionState.Published && (sha256 == null || r.Sha256 == sha256))
                    .Join(
                        context.HlsJobOutputs.AsNoTracking(),
                        r => new { JobId = r.PublishedJobId, r.Key },
                        o => new { JobId = (Guid?)o.JobId, o.Key },
                        (r, o) => new { r.Sha256, r.Key, o.RelativeDirectory, o.Files })
                    .OrderBy(m => m.Sha256)
                    .ThenBy(m => m.Key)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .Select(m => new HlsPublishedManifest(m.Sha256, m.Key, m.RelativeDirectory, Deserialize<List<HlsOutputFile>>(m.Files)))
                .ToList(),
            cancellationToken);

    /// <summary>
    /// Makes a published rendition missing again and queues its directory for deletion. Only the directory that was verified is
    /// retired: a rendition published again in the meantime is left alone.
    /// </summary>
    /// <param name="sha256">The hash of the content.</param>
    /// <param name="key">The key of the rendition.</param>
    /// <param name="expectedDirectory">The directory that was verified.</param>
    /// <param name="reason">Why the rendition is retired.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the rendition was retired.</returns>
    public Task<bool> RetireRenditionAsync(string sha256, string key, string expectedDirectory, string reason, DateTime now, CancellationToken cancellationToken)
        => WriteAsync<bool>(
            async context =>
            {
                var rendition = await context.HlsRenditions.FindAsync([sha256, key], cancellationToken).ConfigureAwait(false);
                if (rendition is null || !string.Equals(rendition.RelativeDirectory, expectedDirectory, StringComparison.Ordinal))
                {
                    return false;
                }

                await QueueDeletionAsync(context, expectedDirectory, now, reason, cancellationToken).ConfigureAwait(false);
                await RemoveOutputAsync(context, rendition.PublishedJobId, key, cancellationToken).ConfigureAwait(false);
                rendition.State = (int)HlsRenditionState.Missing;
                rendition.RelativeDirectory = null;
                rendition.Summary = null;
                rendition.PublishedJobId = null;
                return true;
            },
            cancellationToken);

    // A rendition needs a job when no job holds it and it is missing or published under another encoding profile.
    private static Expression<Func<HlsRendition, bool>> NeedsJob(string profile)
        => r => r.JobId == null
            && (r.State == (int)HlsRenditionState.Missing || (r.State == (int)HlsRenditionState.Published && (r.Profile == null || r.Profile != profile)));

    private static HlsRenditionRecord ToRecord(HlsRendition rendition)
        => new(
            rendition.Sha256,
            rendition.Key,
            (HlsRenditionState)rendition.State,
            rendition.JobId,
            rendition.PublishedJobId,
            rendition.RelativeDirectory,
            rendition.Summary is null ? null : Deserialize<HlsRenditionSummary>(rendition.Summary),
            rendition.Profile);

    private static async Task<bool> SyncRenditionsAsync(JellyfinDbContext context, string sha256, IReadOnlyList<HlsRenditionSpec> desired, DateTime now, CancellationToken cancellationToken)
    {
        var wanted = desired.Select(s => s.Key).Distinct(StringComparer.Ordinal).ToList();
        var stored = await context.HlsRenditions.Where(r => r.Sha256 == sha256).OrderBy(r => r.Key).ToListAsync(cancellationToken).ConfigureAwait(false);
        var changed = false;
        foreach (var rendition in stored.Where(r => !wanted.Contains(r.Key, StringComparer.Ordinal)))
        {
            if (rendition.RelativeDirectory is { } directory)
            {
                await QueueDeletionAsync(context, directory, now, "The rendition is no longer wanted.", cancellationToken).ConfigureAwait(false);
                await RemoveOutputAsync(context, rendition.PublishedJobId, rendition.Key, cancellationToken).ConfigureAwait(false);
            }

            context.HlsRenditions.Remove(rendition);
            changed = true;
        }

        foreach (var key in wanted.Where(k => !stored.Any(r => string.Equals(r.Key, k, StringComparison.Ordinal))))
        {
            context.HlsRenditions.Add(new HlsRendition { Sha256 = sha256, Key = key, State = (int)HlsRenditionState.Missing });
            changed = true;
        }

        return changed;
    }

    // Forgets the manifest of a published rendition.
    private static async Task RemoveOutputAsync(JellyfinDbContext context, Guid? publishedJobId, string key, CancellationToken cancellationToken)
    {
        if (publishedJobId is { } jobId && await context.HlsJobOutputs.FindAsync([jobId, key], cancellationToken).ConfigureAwait(false) is { } output)
        {
            context.HlsJobOutputs.Remove(output);
        }
    }
}
