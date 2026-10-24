using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Hls;

/// <content>
/// The workers, the jobs they give back and the files they write into libraries.
/// </content>
public sealed partial class HlsJobStore
{
    /// <summary>
    /// Registers a worker: one the server knows is seen again, any other is recorded, under the id it has or a new one.
    /// </summary>
    /// <param name="id">The id the worker has, or <c>null</c> for a new one.</param>
    /// <param name="name">The name of the worker.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The id of the worker.</returns>
    /// <exception cref="ArgumentException">The id is empty.</exception>
    public Task<Guid> RegisterWorkerAsync(Guid? id, string name, DateTime now, CancellationToken cancellationToken)
    {
        if (id is { } given && given.Equals(Guid.Empty))
        {
            throw new ArgumentException("The id of a worker is not empty.", nameof(id));
        }

        var key = id ?? Guid.NewGuid();
        return WriteAsync(
            async context =>
            {
                var worker = await context.HlsWorkers.FindAsync([key], cancellationToken).ConfigureAwait(false);
                if (worker is null)
                {
                    context.HlsWorkers.Add(new HlsWorkerIdentity { Id = key, Name = name, DateCreated = now, DateLastSeen = now });
                }
                else
                {
                    worker.Name = name;
                    worker.DateLastSeen = now;
                }

                return key;
            },
            cancellationToken);
    }

    /// <summary>
    /// Gives back a job the worker cannot read the file of, before it was identified: it waits again for a claim without counting
    /// the attempt, and keeps its renditions.
    /// </summary>
    /// <param name="id">The job.</param>
    /// <param name="leaseToken">The token of the lease.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the lease was valid and the job was not identified yet.</returns>
    public Task<bool> ReturnJobAsync(Guid id, string leaseToken, DateTime now, CancellationToken cancellationToken)
        => WriteAsync<bool>(
            async context =>
            {
                var job = await context.HlsJobs.FindAsync([id], cancellationToken).ConfigureAwait(false);
                if (!IsLeaseValid(job, leaseToken, now) || job.Plan is not null)
                {
                    return false;
                }

                Requeue(job, "The worker " + job.Worker + " cannot read the file.", now);
                job.Worker = null;
                job.WorkerId = null;
                return true;
            },
            cancellationToken);

    /// <summary>
    /// Records files a worker wrote into a library folder; a path recorded before is replaced.
    /// </summary>
    /// <param name="files">The files.</param>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task RecordLibraryFilesAsync(IReadOnlyCollection<HlsLibraryFileRecord> files, DateTime now, CancellationToken cancellationToken)
        => WriteAsync(
            async context =>
            {
                var paths = files.Select(f => f.Path).ToList();
                var known = await context.HlsLibraryFiles.Where(f => paths.Contains(f.Path)).ToListAsync(cancellationToken).ConfigureAwait(false);
                context.HlsLibraryFiles.RemoveRange(known);
                foreach (var file in files)
                {
                    context.HlsLibraryFiles.Add(new HlsLibraryFile
                    {
                        Id = Guid.NewGuid(),
                        LibraryId = file.LibraryId,
                        Path = file.Path,
                        Length = file.Length,
                        WorkerId = file.WorkerId,
                        Sha256 = file.Sha256,
                        ContentLength = file.ContentLength,
                        DateCreated = now
                    });
                }

                return true;
            },
            cancellationToken);

    /// <summary>
    /// Gets the content a worker reported for a file it wrote into a library folder.
    /// </summary>
    /// <param name="path">The full path of the file on the server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The file, or null when no worker wrote it as a content.</returns>
    public Task<HlsAddedFile?> GetAddedFileAsync(string path, CancellationToken cancellationToken)
        => ReadAsync(
            async context =>
            {
                var file = await context.HlsLibraryFiles.AsNoTracking()
                    .Where(f => f.Path == path && f.Sha256 != null && f.ContentLength != null)
                    .OrderByDescending(f => f.DateCreated)
                    .FirstOrDefaultAsync(cancellationToken)
                    .ConfigureAwait(false);
                return file is null ? null : new HlsAddedFile(file.Sha256!, file.ContentLength!.Value, file.Length, file.DateCreated);
            },
            cancellationToken);
}
