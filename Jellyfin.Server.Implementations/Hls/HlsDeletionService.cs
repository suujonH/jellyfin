using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Controller.Hls;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// Works through the queue of directories to delete from the HLS store. A failure only postpones its own directory, from one
/// minute up to six hours, and never holds up the jobs.
/// </summary>
public sealed class HlsDeletionService : BackgroundService
{
    private const int BatchSize = 16;
    private const int Parallelism = 4;
    private const double MaxBackoffMinutes = 360;

    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(10);

    private readonly HlsJobStore _store;
    private readonly HlsSettings _settings;
    private readonly HlsRootMonitor _monitor;
    private readonly ILogger<HlsDeletionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsDeletionService"/> class.
    /// </summary>
    /// <param name="store">The data of pre-encoded HLS.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="monitor">The monitor of the root of the HLS store.</param>
    /// <param name="logger">The logger.</param>
    public HlsDeletionService(HlsJobStore store, HlsSettings settings, HlsRootMonitor monitor, ILogger<HlsDeletionService> logger)
    {
        _store = store;
        _settings = settings;
        _monitor = monitor;
        _logger = logger;
    }

    /// <summary>
    /// Deletes the directories that are due. Nothing is touched while the root of the store cannot be trusted, and a deletion
    /// counts as done only when the root can still be trusted afterwards: a directory may only seem gone.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    internal async Task ProcessAsync(DateTime now, CancellationToken cancellationToken)
    {
        var due = await _store.GetDueDeletionsAsync(now, BatchSize, cancellationToken).ConfigureAwait(false);
        var settings = _settings.Current;
        if (due.Count == 0 || await _monitor.CheckAsync(settings, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        var store = settings.Store!;
        var deleted = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(
                due,
                new ParallelOptions { MaxDegreeOfParallelism = Parallelism, CancellationToken = cancellationToken },
                async (deletion, token) =>
                {
                    if (await DeleteAsync(store, deletion, now, token).ConfigureAwait(false))
                    {
                        deleted.Add(deletion.RelativeDirectory);
                    }
                })
            .ConfigureAwait(false);
        if (deleted.IsEmpty || await _monitor.CheckAsync(settings, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        foreach (var directory in deleted)
        {
            await _store.CompleteDeletionAsync(directory, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            using var timer = new PeriodicTimer(_interval);
            do
            {
                try
                {
                    await ProcessAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Working through the HLS deletion queue failed");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // An exception leaving a BackgroundService stops the whole server.
            _logger.LogCritical(ex, "The HLS deletion stopped unexpectedly");
        }
    }

    // Whether the directory was deleted; a failure postpones it.
    private async Task<bool> DeleteAsync(IHlsStore store, HlsDeletionRecord deletion, DateTime now, CancellationToken cancellationToken)
    {
        // Checked again before anything is deleted: only directories of the layout, and of upload checks, are ever touched.
        if (!HlsRenditionPaths.IsValidRelativeDirectory(deletion.RelativeDirectory) && !HlsStorePaths.IsUploadCheckDirectory(deletion.RelativeDirectory))
        {
            _logger.LogError("The HLS deletion queue holds an invalid directory, which is dropped without deleting anything: {Directory}", deletion.RelativeDirectory);
            await _store.CompleteDeletionAsync(deletion.RelativeDirectory, cancellationToken).ConfigureAwait(false);
            return false;
        }

        try
        {
            await store.DeleteDirectoryAsync(deletion.RelativeDirectory, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (IOException ex)
        {
            var next = now + TimeSpan.FromMinutes(Math.Min(MaxBackoffMinutes, Math.Pow(2, deletion.Attempts)));
            await _store.PostponeDeletionAsync(deletion.RelativeDirectory, ex.Message, next, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(ex, "Deleting {Directory} from the HLS store failed, it is tried again at {Next}", deletion.RelativeDirectory, next);
            return false;
        }
    }
}
