using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
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
/// The renditions of the contents.
/// </content>
public sealed partial class HlsJobStore
{
    /// <summary>
    /// Gets every content with what is published of it, the size of each rendition from the files its job submitted, and the
    /// items whose file is the content.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The contents, by hash.</returns>
    public Task<IReadOnlyList<HlsStoredContent>> GetStoredContentsAsync(CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<HlsStoredContent>>(
            async context =>
            {
                var media = await context.HlsMedia.AsNoTracking().OrderBy(m => m.Sha256).ToListAsync(cancellationToken).ConfigureAwait(false);
                var renditions = await context.HlsRenditions.AsNoTracking()
                    .Where(r => r.State == (int)HlsRenditionState.Published && r.Base != null && r.PublishedJobId != null)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                // Only the outputs not read before are read: their file lists, which are the bulk of them, once each.
                var jobIds = renditions
                    .Where(r => !_outputSizes.ContainsKey((r.PublishedJobId!.Value, r.Key)))
                    .Select(r => r.PublishedJobId!.Value)
                    .Distinct()
                    .ToList();
                if (jobIds.Count > 0)
                {
                    var outputs = await context.HlsJobOutputs.AsNoTracking()
                        .Where(o => jobIds.Contains(o.JobId))
                        .Select(o => new { o.JobId, o.Key, o.Files })
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false);
                    foreach (var output in outputs)
                    {
                        var files = Deserialize<List<HlsOutputFile>>(output.Files);
                        _outputSizes[(output.JobId, output.Key)] = (files.Sum(f => f.Length), files.Count);
                    }
                }

                var sources = (await context.HlsSources.AsNoTracking()
                        .Where(s => s.Sha256 != null)
                        .Select(s => new { s.ItemId, s.Sha256 })
                        .ToListAsync(cancellationToken)
                        .ConfigureAwait(false))
                    .ToLookup(s => s.Sha256!, s => s.ItemId, StringComparer.Ordinal);
                var bySha = renditions.ToLookup(r => r.Sha256, StringComparer.Ordinal);
                return media.Select(m => new HlsStoredContent(
                        m.Sha256,
                        m.LibraryId,
                        m.DateOrphaned,
                        bySha[m.Sha256].OrderBy(r => r.Key, StringComparer.Ordinal).Select(ToStored).ToList(),
                        sources[m.Sha256].Order().ToList()))
                    .ToList();
            },
            cancellationToken);

    /// <summary>
    /// Brings the renditions of a content in line with its stored probe and the qualities it should have: renditions no longer
    /// wanted are removed and their directories queued for deletion, new ones are added as missing. A content in no chosen
    /// library is left as it is, for the orphan rules.
    /// </summary>
    /// <param name="sha256">The hash of the content.</param>
    /// <param name="getQualities">The video qualities of a library.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether any rendition was added or removed.</returns>
    public Task<bool> SyncRenditionsAsync(string sha256, Func<Guid, IReadOnlyCollection<int>> getQualities, DateTime now, CancellationToken cancellationToken)
        => WriteAsync<bool>(
            async context =>
            {
                var media = await context.HlsMedia.FindAsync([sha256], cancellationToken).ConfigureAwait(false);
                if (media is null)
                {
                    return false;
                }

                var qualities = await GetContentQualitiesAsync(context, media, getQualities, cancellationToken).ConfigureAwait(false);
                return qualities.Count > 0
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
    /// Gets the segments of a published rendition from the outputs of the job that published it. The published index leaves them
    /// out, as a long film has some 1800 of them in each rendition.
    /// </summary>
    /// <param name="sha256">The hash of the content.</param>
    /// <param name="key">The key of the rendition.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The segments, or <c>null</c> when the rendition is not published or its outputs are gone.</returns>
    public Task<HlsPublishedSegments?> GetPublishedSegmentsAsync(string sha256, string key, CancellationToken cancellationToken)
        => ReadAsync<HlsPublishedSegments?>(
            async context =>
            {
                var rendition = await context.HlsRenditions.AsNoTracking()
                    .Where(r => r.Sha256 == sha256 && r.Key == key && r.State == (int)HlsRenditionState.Published && r.PublishedJobId != null)
                    .Select(r => new { JobId = r.PublishedJobId!.Value, r.RelativeDirectory })
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (rendition is null)
                {
                    return null;
                }

                var output = await context.HlsJobOutputs.AsNoTracking()
                    .Where(o => o.JobId.Equals(rendition.JobId) && o.Key == key)
                    .Select(o => new { o.RelativeDirectory, o.Segments })
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                return output is null || !string.Equals(output.RelativeDirectory, rendition.RelativeDirectory, StringComparison.Ordinal)
                    ? null
                    : new HlsPublishedSegments(output.RelativeDirectory, Deserialize<double[]>(output.Segments));
            },
            cancellationToken);

    /// <summary>
    /// Records the segments of a published rendition that has none, as one migrated from the DistributedHls plugin, which did not
    /// keep them. Only the directory that was read is given them, and segments already recorded are kept.
    /// </summary>
    /// <param name="sha256">The hash of the content.</param>
    /// <param name="key">The key of the rendition.</param>
    /// <param name="relativeDirectory">The directory the segments were read from.</param>
    /// <param name="durations">The duration of every segment in seconds, in order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the segments were recorded.</returns>
    public Task<bool> SetMissingSegmentsAsync(string sha256, string key, string relativeDirectory, IReadOnlyList<double> durations, CancellationToken cancellationToken)
        => WriteAsync<bool>(
            async context =>
            {
                var jobId = await context.HlsRenditions
                    .Where(r => r.Sha256 == sha256 && r.Key == key && r.State == (int)HlsRenditionState.Published && r.RelativeDirectory == relativeDirectory)
                    .Select(r => r.PublishedJobId)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                var output = jobId is null
                    ? null
                    : await context.HlsJobOutputs
                        .FirstOrDefaultAsync(o => o.JobId.Equals(jobId.Value) && o.Key == key && o.RelativeDirectory == relativeDirectory, cancellationToken)
                        .ConfigureAwait(false);
                if (output is null || Deserialize<double[]>(output.Segments).Length > 0)
                {
                    return false;
                }

                output.Segments = JsonSerializer.Serialize(durations);
                return true;
            },
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
    /// Gets the directories published last under a root, which the root must still contain while it has no marker.
    /// </summary>
    /// <param name="root">The root.</param>
    /// <param name="ignoreCase">Whether the root is matched whatever its case, as a local path on Windows is.</param>
    /// <param name="limit">The most directories to return.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The directories.</returns>
    public Task<IReadOnlyList<string>> GetPublishedSampleAsync(string root, bool ignoreCase, int limit, CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<string>>(
            async context => await context.HlsRenditions.AsNoTracking()
                .Where(r => r.State == (int)HlsRenditionState.Published && r.RelativeDirectory != null)
                .Where(r => ignoreCase ? EF.Functions.Collate(r.Base, "NOCASE") == root : r.Base == root)
                .OrderByDescending(r => r.DatePublished)
                .ThenBy(r => r.RelativeDirectory)
                .Take(limit)
                .Select(r => r.RelativeDirectory!)
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),
            cancellationToken);

    /// <summary>
    /// Gets every root in use: of a published rendition, of a job whose outputs wait to be verified, or of a queued deletion.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The roots, in order.</returns>
    public Task<IReadOnlyList<string>> GetRootsAsync(CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<string>>(
            async context =>
            {
                var published = await context.HlsRenditions.AsNoTracking()
                    .Where(r => r.State == (int)HlsRenditionState.Published && r.Base != null)
                    .Select(r => r.Base!)
                    .Distinct()
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                var verifying = await context.HlsJobs.AsNoTracking()
                    .Where(j => j.Status == (int)HlsJobStatus.Verifying && j.Base != null)
                    .Select(j => j.Base!)
                    .Distinct()
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                var deletions = await context.HlsDeletions.AsNoTracking().Select(d => d.Base).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
                return published.Concat(verifying).Concat(deletions).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            },
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
                        (r, o) => new { r.Sha256, r.Key, r.Base, o.RelativeDirectory, o.Files })
                    .OrderBy(m => m.Sha256)
                    .ThenBy(m => m.Key)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false))
                .Select(m => new HlsPublishedManifest(m.Sha256, m.Key, m.Base ?? string.Empty, m.RelativeDirectory, Deserialize<List<HlsOutputFile>>(m.Files)))
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

                await QueueDeletionAsync(context, rendition.Base, expectedDirectory, now, reason, cancellationToken).ConfigureAwait(false);
                await RemoveOutputAsync(context, rendition.PublishedJobId, key, cancellationToken).ConfigureAwait(false);
                rendition.State = (int)HlsRenditionState.Missing;
                rendition.RelativeDirectory = null;
                rendition.Base = null;
                rendition.Summary = null;
                rendition.PublishedJobId = null;
                return true;
            },
            cancellationToken);

    // The video qualities a content should have: those of every library its files are in, or, while it has no file in a chosen
    // library, those of the library it was first seen in. The sources are read from the database, so earlier changes go first.
    private static async Task<IReadOnlyCollection<int>> GetContentQualitiesAsync(JellyfinDbContext context, HlsMedia media, Func<Guid, IReadOnlyCollection<int>> getQualities, CancellationToken cancellationToken)
    {
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        var sha256 = media.Sha256;
        var libraries = await context.HlsSources.Where(s => s.Sha256 == sha256).Select(s => s.LibraryId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
        var qualities = libraries.SelectMany(getQualities).Distinct().ToList();
        return qualities.Count > 0 ? qualities : getQualities(media.LibraryId);
    }

    // A rendition needs a job when no job holds it and it is missing or published under another encoding profile.
    private static Expression<Func<HlsRendition, bool>> NeedsJob(string profile)
        => r => !r.JobId.HasValue
            && (r.State == (int)HlsRenditionState.Missing || (r.State == (int)HlsRenditionState.Published && (r.Profile == null || r.Profile != profile)));

    private static HlsRenditionRecord ToRecord(HlsRendition rendition)
        => new(
            rendition.Sha256,
            rendition.Key,
            (HlsRenditionState)rendition.State,
            rendition.JobId,
            rendition.PublishedJobId,
            rendition.Base,
            rendition.RelativeDirectory,
            rendition.Summary is null ? null : Deserialize<HlsRenditionSummary>(rendition.Summary),
            rendition.Profile);

    // A rendition whose outputs are gone has no size known.
    private HlsStoredRendition ToStored(HlsRendition rendition)
    {
        var (size, count) = _outputSizes.GetValueOrDefault((rendition.PublishedJobId!.Value, rendition.Key));
        return new HlsStoredRendition(
            rendition.Key,
            GetKind(rendition.Key),
            rendition.Base!,
            rendition.Summary is null ? new HlsRenditionSummary() : Deserialize<HlsRenditionSummary>(rendition.Summary),
            size,
            count);
    }

    // The kind of a rendition by its key, as HlsRenditionPlanner names them: v1080, a1, s2 and preview.
    private static HlsRenditionKind GetKind(string key)
        => string.Equals(key, "preview", StringComparison.Ordinal)
            ? HlsRenditionKind.Preview
            : key[0] switch
            {
                'a' => HlsRenditionKind.Audio,
                's' => HlsRenditionKind.Subtitle,
                _ => HlsRenditionKind.Video
            };

    private static async Task<bool> SyncRenditionsAsync(JellyfinDbContext context, string sha256, IReadOnlyList<HlsRenditionSpec> desired, DateTime now, CancellationToken cancellationToken)
    {
        var wanted = desired.Select(s => s.Key).Distinct(StringComparer.Ordinal).ToList();
        var stored = await context.HlsRenditions.Where(r => r.Sha256 == sha256).OrderBy(r => r.Key).ToListAsync(cancellationToken).ConfigureAwait(false);
        var changed = false;
        foreach (var rendition in stored.Where(r => !wanted.Contains(r.Key, StringComparer.Ordinal)))
        {
            if (rendition.RelativeDirectory is { } directory)
            {
                await QueueDeletionAsync(context, rendition.Base, directory, now, "The rendition is no longer wanted.", cancellationToken).ConfigureAwait(false);
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
