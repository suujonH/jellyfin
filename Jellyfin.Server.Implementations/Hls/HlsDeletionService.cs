using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Controller.Hls;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// Works through the queue of directories to delete from the HLS stores. A failure only postpones its own directory, from one
/// minute up to six hours, and never holds up the jobs.
/// </summary>
public sealed class HlsDeletionService : BackgroundService
{
    private const int BatchSize = 16;
    private const int Parallelism = 4;
    private const double MaxBackoffMinutes = 360;

    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(10);

    private readonly HlsJobStore _store;
    private readonly HlsRootMonitor _monitor;
    private readonly ILogger<HlsDeletionService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsDeletionService"/> class.
    /// </summary>
    /// <param name="store">The data of pre-encoded HLS.</param>
    /// <param name="monitor">The monitor of the roots of the HLS stores.</param>
    /// <param name="logger">The logger.</param>
    public HlsDeletionService(HlsJobStore store, HlsRootMonitor monitor, ILogger<HlsDeletionService> logger)
    {
        _store = store;
        _monitor = monitor;
        _logger = logger;
    }

    /// <summary>
    /// Deletes the directories that are due, root by root. Nothing is touched in a root that cannot be trusted, and the batch is
    /// then read again without it, so the other roots are not held up. A deletion counts as done only when its root can still be
    /// trusted afterwards: a directory may only seem gone.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    internal async Task ProcessAsync(DateTime now, CancellationToken cancellationToken)
    {
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        bool skippedMore;
        do
        {
            // Every round skips one root more or is the last, so this ends.
            skippedMore = false;
            var due = await _store.GetDueDeletionsAsync(now, BatchSize, skipped, cancellationToken).ConfigureAwait(false);
            foreach (var root in due.GroupBy(d => d.Base, StringComparer.Ordinal))
            {
                if (!await ProcessRootAsync(root.Key, root.ToList(), now, cancellationToken).ConfigureAwait(false))
                {
                    skippedMore |= skipped.Add(root.Key);
                }
            }
        }
        while (skippedMore);
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

    // Whether the root could be worked on; one that cannot be trusted is left as it is.
    private async Task<bool> ProcessRootAsync(string root, IReadOnlyList<HlsDeletionRecord> due, DateTime now, CancellationToken cancellationToken)
    {
        IHlsStore store;
        try
        {
            store = _monitor.Open(root);
        }
        catch (ArgumentException)
        {
            // Nothing is deleted where it is not known what the directory is relative to.
            _logger.LogError("The HLS deletion queue holds directories of an invalid root, which are dropped without deleting anything: {Root}", root);
            foreach (var deletion in due)
            {
                await _store.CompleteDeletionAsync(deletion.RelativeDirectory, cancellationToken).ConfigureAwait(false);
            }

            return true;
        }

        if (await _monitor.CheckAsync(root, cancellationToken).ConfigureAwait(false) is not null)
        {
            return false;
        }

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
        if (deleted.IsEmpty)
        {
            return true;
        }

        if (await _monitor.CheckAsync(root, cancellationToken).ConfigureAwait(false) is not null)
        {
            return false;
        }

        foreach (var directory in deleted)
        {
            await _store.CompleteDeletionAsync(directory, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    // Whether the directory was deleted; a failure postpones it.
    private async Task<bool> DeleteAsync(IHlsStore store, HlsDeletionRecord deletion, DateTime now, CancellationToken cancellationToken)
    {
        // Checked again before anything is deleted: only directories of the layout are ever touched.
        if (!HlsRenditionPaths.IsValidRelativeDirectory(deletion.RelativeDirectory))
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
            _logger.LogWarning(ex, "Deleting {Directory} from the HLS store {Root} failed, it is tried again at {Next}", deletion.RelativeDirectory, deletion.Base, next);
            return false;
        }
    }
}
