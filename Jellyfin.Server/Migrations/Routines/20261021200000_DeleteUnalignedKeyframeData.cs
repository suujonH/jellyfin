using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Server.Migrations.Stages;
using Jellyfin.Server.ServerSetupApp;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Migrations.Routines;

/// <summary>
/// Deletes the stored keyframes. ffprobe stored absolute timestamps, which are off by the start time of MPEG-TS, MPEG-PS and
/// similar containers and of an MP4 whose tracks all start late; keyframes are now relative to the format start time. The
/// container does not tell which lists are off, and live HLS reads them again when needed.
/// </summary>
[JellyfinMigration("2026-10-21T20:00:00", nameof(DeleteUnalignedKeyframeData), Stage = JellyfinMigrationStageTypes.AppInitialisation)]
internal class DeleteUnalignedKeyframeData : IAsyncMigrationRoutine
{
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeleteUnalignedKeyframeData"/> class.
    /// </summary>
    /// <param name="dbProvider">The database context factory.</param>
    /// <param name="startupLogger">The startup logger for Startup UI integration.</param>
    /// <param name="logger">The logger.</param>
    public DeleteUnalignedKeyframeData(
        IDbContextFactory<JellyfinDbContext> dbProvider,
        IStartupLogger<DeleteUnalignedKeyframeData> startupLogger,
        ILogger<DeleteUnalignedKeyframeData> logger)
    {
        _dbProvider = dbProvider;
        _logger = startupLogger.With(logger);
    }

    /// <inheritdoc />
    public async Task PerformAsync(CancellationToken cancellationToken)
    {
        var context = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var count = await context.KeyframeData.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Deleted the stored keyframes of {Count} videos; they are read again when needed", count);
        }
    }
}
