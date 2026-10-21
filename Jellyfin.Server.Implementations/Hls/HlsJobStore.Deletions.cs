using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Hls;

/// <content>
/// The queue of directories to delete.
/// </content>
public sealed partial class HlsJobStore
{
    /// <summary>
    /// Gets the directories that are due to be deleted, the longest due first.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="limit">The most directories to return.</param>
    /// <param name="skippedRoots">The roots whose directories are left out.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The directories.</returns>
    public Task<IReadOnlyList<HlsDeletionRecord>> GetDueDeletionsAsync(DateTime now, int limit, IReadOnlyCollection<string> skippedRoots, CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<HlsDeletionRecord>>(
            async context => await context.HlsDeletions.AsNoTracking()
                .Where(d => d.NotBefore <= now && !skippedRoots.Contains(d.Base))
                .OrderBy(d => d.NotBefore)
                .ThenBy(d => d.RelativeDirectory)
                .Take(limit)
                .Select(d => new HlsDeletionRecord(d.Base, d.RelativeDirectory, d.Attempts))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false),
            cancellationToken);

    /// <summary>
    /// Removes a deleted directory from the queue.
    /// </summary>
    /// <param name="relativeDirectory">The directory.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task CompleteDeletionAsync(string relativeDirectory, CancellationToken cancellationToken)
        => WriteAsync(
            async context =>
            {
                if (await context.HlsDeletions.FindAsync([relativeDirectory], cancellationToken).ConfigureAwait(false) is { } deletion)
                {
                    context.HlsDeletions.Remove(deletion);
                }

                return true;
            },
            cancellationToken);

    /// <summary>
    /// Records that deleting a directory failed and when to try again.
    /// </summary>
    /// <param name="relativeDirectory">The directory.</param>
    /// <param name="error">Why deleting failed.</param>
    /// <param name="next">When to try again.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task PostponeDeletionAsync(string relativeDirectory, string error, DateTime next, CancellationToken cancellationToken)
        => WriteAsync(
            async context =>
            {
                if (await context.HlsDeletions.FindAsync([relativeDirectory], cancellationToken).ConfigureAwait(false) is { } deletion)
                {
                    deletion.Attempts++;
                    deletion.LastError = error;
                    deletion.NotBefore = next;
                }

                return true;
            },
            cancellationToken);
}
