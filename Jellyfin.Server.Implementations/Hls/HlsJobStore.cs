using System;
using System.Collections.Generic;
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

/// <summary>
/// The data of pre-encoded HLS in the database: library files, contents, renditions, jobs, their outputs and the deletion queue.
/// </summary>
/// <remarks>
/// Every write runs in one transaction under one lock, so this store is the only writer of its tables and decides every
/// transition on what its transaction read; reads take no lock. Times are UTC and passed in, JSON columns use the default
/// options of System.Text.Json, as the DistributedHls plugin did.
/// </remarks>
public sealed partial class HlsJobStore : IDisposable
{
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsJobStore"/> class.
    /// </summary>
    /// <param name="dbProvider">The database.</param>
    public HlsJobStore(IDbContextFactory<JellyfinDbContext> dbProvider)
    {
        _dbProvider = dbProvider;
    }

    /// <inheritdoc />
    public void Dispose() => _writeLock.Dispose();

    /// <summary>
    /// Records library files. A file whose path, size or modification time changed is new content, so its hash is cleared;
    /// every file left without a hash gets a source job in the same transaction.
    /// </summary>
    /// <param name="files">The files.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What changed, for every file in the order given.</returns>
    public Task<IReadOnlyList<HlsUpsertResult>> UpsertSourcesAsync(IReadOnlyList<HlsSourceFile> files, DateTime now, CancellationToken cancellationToken)
        => WriteAsync<IReadOnlyList<HlsUpsertResult>>(
            async context =>
            {
                var results = new List<HlsUpsertResult>(files.Count);
                var unhashed = new HashSet<Guid>();
                foreach (var file in files)
                {
                    // Finds a row added for the same item earlier in this call as well.
                    var stored = await context.HlsSources.FindAsync([file.ItemId], cancellationToken).ConfigureAwait(false);
                    if (stored is null)
                    {
                        stored = new HlsSource { ItemId = file.ItemId, LibraryId = file.LibraryId, Path = file.Path, Length = file.Length, DateModified = file.DateModified };
                        context.HlsSources.Add(stored);
                        results.Add(new HlsUpsertResult(file.ItemId, HlsSourceChange.Inserted, null));
                    }
                    else if (IsSameFile(stored, file))
                    {
                        stored.LibraryId = file.LibraryId;
                        results.Add(new HlsUpsertResult(file.ItemId, HlsSourceChange.Unchanged, stored.Sha256));
                    }
                    else
                    {
                        results.Add(new HlsUpsertResult(file.ItemId, HlsSourceChange.ContentChanged, stored.Sha256));
                        stored.LibraryId = file.LibraryId;
                        stored.Path = file.Path;
                        stored.Length = file.Length;
                        stored.DateModified = file.DateModified;
                        stored.Sha256 = null;
                    }

                    if (stored.Sha256 is null)
                    {
                        unhashed.Add(file.ItemId);
                    }
                }

                await EnsureSourceJobsAsync(context, unhashed, now, cancellationToken).ConfigureAwait(false);
                return results;
            },
            cancellationToken);

    /// <summary>
    /// Forgets a library file and cancels its pending source job. Its content stays until it expires as an orphan.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The hash the file had.</returns>
    public Task<string?> RemoveSourceAsync(Guid itemId, DateTime now, CancellationToken cancellationToken)
        => WriteAsync(
            async context =>
            {
                var stored = await context.HlsSources.FindAsync([itemId], cancellationToken).ConfigureAwait(false);
                if (stored is not null)
                {
                    context.HlsSources.Remove(stored);
                }

                var workKey = GetSourceWorkKey(itemId);
                var job = await context.HlsJobs
                    .FirstOrDefaultAsync(j => j.WorkKey == workKey && j.Status == (int)HlsJobStatus.Pending, cancellationToken)
                    .ConfigureAwait(false);
                if (job is not null)
                {
                    await CancelAsync(context, job, now, cancellationToken).ConfigureAwait(false);
                }

                return stored?.Sha256;
            },
            cancellationToken);

    /// <summary>
    /// Creates a source job for every library file without a hash that has none, for a file whose content changed while its
    /// previous job was running.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task EnsureSourceJobsAsync(DateTime now, CancellationToken cancellationToken)
        => WriteAsync(
            async context =>
            {
                var items = await context.HlsSources.Where(s => s.Sha256 == null).Select(s => s.ItemId).ToListAsync(cancellationToken).ConfigureAwait(false);
                await EnsureSourceJobsAsync(context, items, now, cancellationToken).ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Gets a recorded library file.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The file, or null.</returns>
    public Task<HlsSourceRecord?> GetSourceAsync(Guid itemId, CancellationToken cancellationToken)
        => ReadAsync(
            async context => await context.HlsSources.AsNoTracking().Where(s => s.ItemId.Equals(itemId)).Select(s => ToRecord(s)).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken);

    /// <summary>
    /// Gets every recorded library file.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The files, by item.</returns>
    public Task<IReadOnlyList<HlsSourceRecord>> GetSourcesAsync(CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<HlsSourceRecord>>(
            async context => await context.HlsSources.AsNoTracking().OrderBy(s => s.ItemId).Select(s => ToRecord(s)).ToListAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken);

    /// <summary>
    /// Gets the library files of a content.
    /// </summary>
    /// <param name="sha256">The hash of the content.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The files, by item.</returns>
    public Task<IReadOnlyList<HlsSourceRecord>> GetSourcesByShaAsync(string sha256, CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<HlsSourceRecord>>(
            async context => await context.HlsSources.AsNoTracking()
                .Where(s => s.Sha256 == sha256)
                .OrderBy(s => s.ItemId)
                .Select(s => ToRecord(s))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),
            cancellationToken);

    /// <summary>
    /// Gets an identified content.
    /// </summary>
    /// <param name="sha256">The hash.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The content, or null.</returns>
    public Task<HlsMediaRecord?> GetMediaAsync(string sha256, CancellationToken cancellationToken)
        => ReadAsync(
            async context =>
            {
                var media = await context.HlsMedia.AsNoTracking().FirstOrDefaultAsync(m => m.Sha256 == sha256, cancellationToken).ConfigureAwait(false);
                return media is null ? null : ToRecord(media);
            },
            cancellationToken);

    /// <summary>
    /// Gets the hash of every identified content.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The hashes, in order.</returns>
    public Task<IReadOnlyList<string>> GetMediaShasAsync(CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<string>>(
            async context => await context.HlsMedia.AsNoTracking().OrderBy(m => m.Sha256).Select(m => m.Sha256).ToListAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken);

    /// <summary>
    /// Marks contents without a library file as orphans, from now on, and those that have one again as no orphans.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task RefreshOrphansAsync(DateTime now, CancellationToken cancellationToken)
        => WriteAsync(
            async context =>
            {
                await context.HlsMedia
                    .Where(m => m.DateOrphaned == null && !context.HlsSources.Any(s => s.Sha256 == m.Sha256))
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.DateOrphaned, now), cancellationToken)
                    .ConfigureAwait(false);
                await context.HlsMedia
                    .Where(m => m.DateOrphaned != null && context.HlsSources.Any(s => s.Sha256 == m.Sha256))
                    .ExecuteUpdateAsync(s => s.SetProperty(m => m.DateOrphaned, (DateTime?)null), cancellationToken)
                    .ConfigureAwait(false);
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Deletes the contents that have been orphans since the cutoff, with their renditions, jobs and outputs; their directories
    /// are queued for deletion. A content is kept while a job may still produce or own outputs of it: a running or verifying job,
    /// or a cancelled one whose worker still holds the lease. Pending jobs own nothing and go with the content.
    /// </summary>
    /// <param name="cutoff">The latest time of becoming an orphan that is deleted.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The hashes of the deleted contents.</returns>
    public Task<IReadOnlyList<string>> DeleteExpiredOrphansAsync(DateTime cutoff, DateTime now, CancellationToken cancellationToken)
        => WriteAsync<IReadOnlyList<string>>(
            async context =>
            {
                var shas = await context.HlsMedia
                    .Where(m => m.DateOrphaned <= cutoff
                        && !context.HlsSources.Any(s => s.Sha256 == m.Sha256)
                        && !context.HlsJobs.Any(j => j.Sha256 == m.Sha256
                            && (j.Status == (int)HlsJobStatus.Running
                                || j.Status == (int)HlsJobStatus.Verifying
                                || (j.Status == (int)HlsJobStatus.Cancelled && j.LeaseExpires != null))))
                    .OrderBy(m => m.Sha256)
                    .Select(m => m.Sha256)
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                foreach (var sha in shas)
                {
                    var renditions = await context.HlsRenditions.Where(r => r.Sha256 == sha).OrderBy(r => r.Key).ToListAsync(cancellationToken).ConfigureAwait(false);
                    foreach (var rendition in renditions.Where(r => r.RelativeDirectory is not null))
                    {
                        await QueueDeletionAsync(context, rendition.Base, rendition.RelativeDirectory!, now, "The content has no library file left.", cancellationToken).ConfigureAwait(false);
                    }

                    var jobs = await context.HlsJobs.Where(j => j.Sha256 == sha).ToListAsync(cancellationToken).ConfigureAwait(false);

                    // Published outputs may belong to an earlier run of the job: a job created again gets a new id.
                    var owners = jobs.Select(j => j.Id).Concat(renditions.Select(r => r.PublishedJobId).OfType<Guid>()).Distinct().ToList();
                    context.HlsJobOutputs.RemoveRange(await context.HlsJobOutputs.Where(o => owners.Contains(o.JobId)).ToListAsync(cancellationToken).ConfigureAwait(false));
                    context.HlsJobs.RemoveRange(jobs);
                    context.HlsRenditions.RemoveRange(renditions);
                    context.HlsMedia.Remove(await context.HlsMedia.SingleAsync(m => m.Sha256 == sha, cancellationToken).ConfigureAwait(false));
                }

                return shas;
            },
            cancellationToken);

    private static string GetSourceWorkKey(Guid itemId) => "source:" + itemId.ToString("N", CultureInfo.InvariantCulture);

    private static string GetMediaWorkKey(string sha256) => "media:" + sha256;

    private static bool IsSameFile(HlsSource stored, HlsSourceFile file)
        => string.Equals(stored.Path, file.Path, StringComparison.Ordinal) && stored.Length == file.Length && stored.DateModified == file.DateModified;

    private static HlsSourceRecord ToRecord(HlsSource source)
        => new(new HlsSourceFile(source.ItemId, source.LibraryId, source.Path, source.Length, source.DateModified), source.Sha256);

    private static HlsMediaRecord ToRecord(HlsMedia media)
        => new(media.Sha256, media.MediaKey, media.LibraryId, Deserialize<HlsProbe>(media.Probe), media.DateOrphaned);

    private static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json) ?? throw new JsonException("The stored value is null.");

    // A job of a file without a hash: pending, running and verifying jobs and those whose worker still holds the lease stay as
    // they are, a failed one waits for a rebuild; a complete or cancelled one is created again with a new id, since the outputs
    // it may have published are kept under its id. A job holds its lease while LeaseExpires is set: submitting outputs ends it.
    private static async Task EnsureSourceJobsAsync(JellyfinDbContext context, IReadOnlyCollection<Guid> itemIds, DateTime now, CancellationToken cancellationToken)
    {
        if (itemIds.Count == 0)
        {
            return;
        }

        var keys = itemIds.Select(GetSourceWorkKey).ToList();
        var existing = await context.HlsJobs.Where(j => keys.Contains(j.WorkKey)).ToDictionaryAsync(j => j.WorkKey, cancellationToken).ConfigureAwait(false);
        var created = new List<HlsJob>();
        foreach (var itemId in itemIds)
        {
            var key = GetSourceWorkKey(itemId);
            if (existing.TryGetValue(key, out var job))
            {
                if (!CanBeCreatedAgain(job))
                {
                    continue;
                }

                context.HlsJobs.Remove(job);
            }

            created.Add(NewJob(HlsJobKind.Source, key, itemId, null, now));
        }

        // The old rows go first: the new ones have the same work keys.
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        context.HlsJobs.AddRange(created);
    }

    private static bool CanBeCreatedAgain(HlsJob job)
        => job.Status == (int)HlsJobStatus.Complete || (job.Status == (int)HlsJobStatus.Cancelled && job.LeaseExpires is null);

    private static HlsJob NewJob(HlsJobKind kind, string workKey, Guid? itemId, string? sha256, DateTime now)
        => new()
        {
            Id = Guid.NewGuid(),
            Kind = (int)kind,
            WorkKey = workKey,
            ItemId = itemId,
            Sha256 = sha256,
            Status = (int)HlsJobStatus.Pending,
            DateCreated = now,
            DateModified = now
        };

    // Runs inside the caller's transaction. Only directories of the layout are ever queued; a queued directory keeps its earliest time.
    // The lease token in a directory makes it unique across roots. A row from before the roots has none and is queued with an empty
    // one, which the deletion drops without deleting anything.
    private static async Task QueueDeletionAsync(JellyfinDbContext context, string? root, string relativeDirectory, DateTime notBefore, string reason, CancellationToken cancellationToken)
    {
        if (!HlsRenditionPaths.IsValidRelativeDirectory(relativeDirectory))
        {
            throw new ArgumentException("Only rendition directories can be queued for deletion: " + relativeDirectory, nameof(relativeDirectory));
        }

        var row = await context.HlsDeletions.FindAsync([relativeDirectory], cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            context.HlsDeletions.Add(new HlsDeletion { RelativeDirectory = relativeDirectory, Base = root ?? string.Empty, NotBefore = notBefore, Reason = reason });
        }
        else if (notBefore < row.NotBefore)
        {
            row.NotBefore = notBefore;
        }
    }

    // Runs the body in one transaction under the write lock: committed when it returns, rolled back when it throws.
    private async Task<T> WriteAsync<T>(Func<JellyfinDbContext, Task<T>> body, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
                await using (transaction.ConfigureAwait(false))
                {
                    var result = await body(context).ConfigureAwait(false);
                    await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                    return result;
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private Task WriteAsync(Func<JellyfinDbContext, Task<bool>> body, CancellationToken cancellationToken) => WriteAsync<bool>(body, cancellationToken);

    private async Task<T> ReadAsync<T>(Func<JellyfinDbContext, Task<T>> body, CancellationToken cancellationToken)
    {
        var context = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            return await body(context).ConfigureAwait(false);
        }
    }
}
