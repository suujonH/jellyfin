using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.Hls;

/// <content>
/// The version items: one per published quality of an item and one for "auto".
/// </content>
public sealed partial class HlsJobStore
{
    /// <summary>
    /// Gets every version item.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The version items.</returns>
    public Task<IReadOnlyList<HlsVersionItem>> GetVersionItemsAsync(CancellationToken cancellationToken)
        => ReadAsync<IReadOnlyList<HlsVersionItem>>(
            async context => await context.HlsVersionItems.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false),
            cancellationToken);

    /// <summary>
    /// Records a version item, or what it was written from.
    /// </summary>
    /// <param name="itemId">The original item.</param>
    /// <param name="quality">The quality, or 0 for "auto".</param>
    /// <param name="versionId">The id of the version item.</param>
    /// <param name="signature">What the version item was written from.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task SetVersionItemAsync(Guid itemId, int quality, Guid versionId, string signature, CancellationToken cancellationToken)
        => WriteAsync(
            async context =>
            {
                var row = await context.HlsVersionItems.FindAsync([itemId, quality], cancellationToken).ConfigureAwait(false);
                if (row is null)
                {
                    context.HlsVersionItems.Add(new HlsVersionItem { ItemId = itemId, Quality = quality, VersionId = versionId, Signature = signature });
                }
                else
                {
                    row.VersionId = versionId;
                    row.Signature = signature;
                }

                return true;
            },
            cancellationToken);

    /// <summary>
    /// Forgets a version item.
    /// </summary>
    /// <param name="itemId">The original item.</param>
    /// <param name="quality">The quality, or 0 for "auto".</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public Task RemoveVersionItemAsync(Guid itemId, int quality, CancellationToken cancellationToken)
        => WriteAsync(
            async context =>
            {
                var row = await context.HlsVersionItems.FindAsync([itemId, quality], cancellationToken).ConfigureAwait(false);
                if (row is not null)
                {
                    context.HlsVersionItems.Remove(row);
                }

                return true;
            },
            cancellationToken);
}
