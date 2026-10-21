using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Hls;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// Keeps the data of pre-encoded HLS in line with the library and the settings, and creates the jobs: every five seconds it
/// applies changed settings, records the library files that changed, refreshes what the published index missed, reaps expired
/// leases and schedules the contents queued for it; every ten minutes it schedules everything that needs a job, retries the files and contents that failed to be
/// recorded, and deletes expired orphans.
/// </summary>
/// <remarks>
/// One loop owns the scheduling state; library events, followed from the start, only record what happened. Every step is
/// isolated: a failure is logged and never turns into a full scan.
/// </remarks>
public sealed class HlsCoordinator : BackgroundService
{
    private const int UpsertBatch = 500;

    private static readonly TimeSpan _tickInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _sweepInterval = TimeSpan.FromMinutes(10);

    private readonly HlsJobStore _store;
    private readonly HlsSettings _settings;
    private readonly HlsPublishedIndex _index;
    private readonly ILibraryManager _libraryManager;
    private readonly INetworkStorageManager _networkStorage;
    private readonly ITrueCacheService _trueCache;
    private readonly ILogger<HlsCoordinator> _logger;

    // Item → whether it still exists.
    private readonly ConcurrentDictionary<Guid, bool> _changes = new();
    private readonly ConcurrentDictionary<string, byte> _toSchedule = new(StringComparer.Ordinal);

    // Only the loop uses these: the changes and contents a failure left behind, until the next sweep tries them again.
    private readonly Dictionary<Guid, bool> _failedChanges = [];
    private readonly HashSet<string> _unsynced = new(StringComparer.Ordinal);
    private int _rebuild;
    private AppliedSettings? _applied;
    private DateTime _nextSweep;
    private DateTime _nextOrphanRun;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsCoordinator"/> class.
    /// </summary>
    /// <param name="store">The data of pre-encoded HLS.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="index">The published index.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="networkStorage">The network storage.</param>
    /// <param name="trueCache">The <c>__true_cache__</c> lookup.</param>
    /// <param name="logger">The logger.</param>
    public HlsCoordinator(
        HlsJobStore store,
        HlsSettings settings,
        HlsPublishedIndex index,
        ILibraryManager libraryManager,
        INetworkStorageManager networkStorage,
        ITrueCacheService trueCache,
        ILogger<HlsCoordinator> logger)
    {
        _store = store;
        _settings = settings;
        _index = index;
        _libraryManager = libraryManager;
        _networkStorage = networkStorage;
        _trueCache = trueCache;
        _logger = logger;
        _libraryManager.ItemAdded += OnItemChanged;
        _libraryManager.ItemUpdated += OnItemChanged;
        _libraryManager.ItemRemoved += OnItemRemoved;
    }

    /// <summary>
    /// Asks for a rebuild of the work list on the next tick: pending jobs are cancelled, failed renditions become missing again
    /// and everything is scheduled again; published renditions are kept.
    /// </summary>
    public void RequestRebuild() => Volatile.Write(ref _rebuild, 1);

    /// <summary>
    /// Queues a content to be scheduled on the next tick.
    /// </summary>
    /// <param name="sha256">The hash of the content.</param>
    public void EnqueueSchedule(string sha256) => _toSchedule[sha256] = 0;

    /// <inheritdoc />
    public override void Dispose()
    {
        _libraryManager.ItemAdded -= OnItemChanged;
        _libraryManager.ItemUpdated -= OnItemChanged;
        _libraryManager.ItemRemoved -= OnItemRemoved;
        base.Dispose();
    }

    /// <summary>
    /// Runs one round of the loop.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    internal async Task TickAsync(DateTime now, CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        await StepAsync("apply the settings", () => ApplySettingsAsync(settings, now, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (settings.Options.Enabled)
        {
            await StepAsync("record the library changes", () => ProcessLibraryChangesAsync(settings, now, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        await StepAsync("refresh the published index", () => _index.RefreshStaleAsync(cancellationToken), cancellationToken).ConfigureAwait(false);

        await StepAsync("reap the expired leases", () => _store.ReapExpiredLeasesAsync(settings.Options.MaxAttempts, settings.Lease, now, cancellationToken), cancellationToken).ConfigureAwait(false);
        await StepAsync("schedule", () => ScheduleQueuedAsync(settings, now, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (settings.Options.Enabled && now >= _nextSweep)
        {
            await StepAsync("schedule everything", () => SweepAsync(settings, now, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        if (now >= _nextOrphanRun)
        {
            await StepAsync("delete the expired orphans", () => CollectOrphansAsync(settings, now, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Hands control back to the host at once: the first round goes through the whole library.
        await Task.Yield();
        try
        {
            await StepAsync("load the published index", () => _index.LoadAsync(stoppingToken), stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(_tickInterval);
            do
            {
                await TickAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // An exception leaving a BackgroundService stops the whole server.
            _logger.LogCritical(ex, "The HLS coordinator stopped unexpectedly");
        }
    }

    // Item dates are UTC; one read back from the database may have lost its kind.
    private static DateTime ToUniversalTime(DateTime value)
        => value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static bool IsHttpAddress(string? address)
        => Uri.TryCreate(address, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private void OnItemChanged(object? sender, ItemChangeEventArgs e) => Track(e.Item, true);

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e) => Track(e.Item, false);

    private void Track(BaseItem? item, bool exists)
    {
        if (item is Video)
        {
            _changes[item.Id] = exists;
        }
    }

    private async Task StepAsync(string name, Func<Task> step, CancellationToken cancellationToken)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "The HLS coordinator failed to {Step}", name);
        }
    }

    private async Task ApplySettingsAsync(HlsSettingsSnapshot settings, DateTime now, CancellationToken cancellationToken)
    {
        var rebuild = Volatile.Read(ref _rebuild) == 1;
        var last = _applied;
        if (last is not null && last.Version == settings.Version && !rebuild)
        {
            return;
        }

        var options = settings.Options;
        var next = new AppliedSettings(settings.Version, options.Enabled, [.. options.Libraries.Order()], [.. options.Qualities.Order()], options.Store);
        if (!next.Enabled)
        {
            // Turning it off empties the work list; published renditions stay. A rebuild asked for meanwhile still resets the failed renditions.
            await _store.CancelAllJobsAsync(now, cancellationToken).ConfigureAwait(false);
            if (rebuild)
            {
                await _store.ResetForRebuildAsync(now, cancellationToken).ConfigureAwait(false);
            }

            Applied(next, rebuild);
            return;
        }

        // Also when just turned on: while it is off, the qualities are recorded without being applied.
        if (last is null || rebuild || !last.Enabled || !last.Qualities.SequenceEqual(next.Qualities))
        {
            foreach (var sha in await _store.GetMediaShasAsync(cancellationToken).ConfigureAwait(false))
            {
                await SyncRenditionsAsync(sha, settings, now, cancellationToken).ConfigureAwait(false);
            }
        }

        if (rebuild)
        {
            await _store.ResetForRebuildAsync(now, cancellationToken).ConfigureAwait(false);
            _nextSweep = now;
        }

        if (last is null || rebuild || !last.Enabled || !last.Libraries.SequenceEqual(next.Libraries))
        {
            await FullScanAsync(settings, now, cancellationToken).ConfigureAwait(false);
        }

        if (last is not null && !string.Equals(last.Store, next.Store, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "The HLS store changed from {Old} to {New}. Published renditions are read from the new store by their relative paths, so copy them over together with the marker {Marker}. To start over in the new store instead, create the marker there and verify the published renditions: the missing ones are encoded again, and the old store is not cleaned up",
                last.Store,
                next.Store,
                HlsStorePaths.MarkerName);
        }

        Applied(next, rebuild);
    }

    private void Applied(AppliedSettings next, bool rebuild)
    {
        _applied = next;
        if (rebuild)
        {
            Volatile.Write(ref _rebuild, 0);
        }
    }

    // A content that fails is synced again by the next sweep: the sweep only finds renditions that exist.
    private async Task SyncRenditionsAsync(string sha256, HlsSettingsSnapshot settings, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            if (await _store.SyncRenditionsAsync(sha256, settings.Options.Qualities, now, cancellationToken).ConfigureAwait(false))
            {
                await _index.RefreshMediaAsync([sha256], cancellationToken).ConfigureAwait(false);
            }

            _unsynced.Remove(sha256);
            _toSchedule[sha256] = 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _unsynced.Add(sha256);
            _logger.LogWarning(ex, "Syncing the HLS renditions of {Sha256} failed", sha256);
        }
    }

    private async Task FullScanAsync(HlsSettingsSnapshot settings, DateTime now, CancellationToken cancellationToken)
    {
        // Every item is read again; a scan that fails is repeated on the next tick.
        _failedChanges.Clear();
        var seen = new HashSet<Guid>();
        try
        {
            var batch = new List<HlsSourceFile>(UpsertBatch);

            // Owned items and alternate versions too, so a scan sees every file a library event can report.
            var query = new InternalItemsQuery
            {
                MediaTypes = [MediaType.Video],
                Recursive = true,
                IsVirtualItem = false,
                IncludeOwnedItems = true,
                IncludeAlternateVersions = true
            };
            foreach (var video in _libraryManager.GetItemList(query).OfType<Video>())
            {
                HlsSourceFile? file;
                try
                {
                    file = GetSourceFile(video, settings);
                }
                catch (Exception ex)
                {
                    // Not checked is not gone: the recorded file is kept until the sweep checks it again.
                    _logger.LogWarning(ex, "Checking item {ItemId} for HLS failed", video.Id);
                    seen.Add(video.Id);
                    _failedChanges[video.Id] = true;
                    continue;
                }

                if (file is null)
                {
                    continue;
                }

                seen.Add(file.ItemId);
                batch.Add(file);
                if (batch.Count == UpsertBatch)
                {
                    await UpsertAsync(batch, now, cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                await UpsertAsync(batch, now, cancellationToken).ConfigureAwait(false);
            }

            foreach (var stale in (await _store.GetSourcesAsync(cancellationToken).ConfigureAwait(false)).Where(s => !seen.Contains(s.File.ItemId)))
            {
                await RemoveSourceAsync(stale.File.ItemId, now, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            // Also after a failure: whatever was committed has to reach the index.
            await _index.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessLibraryChangesAsync(HlsSettingsSnapshot settings, DateTime now, CancellationToken cancellationToken)
    {
        var processed = new List<Guid>();
        foreach (var itemId in _changes.Keys.Take(UpsertBatch).ToList())
        {
            if (!_changes.TryRemove(itemId, out var exists))
            {
                continue;
            }

            processed.Add(itemId);
            try
            {
                if (exists && _libraryManager.GetItemById(itemId) is Video video && GetSourceFile(video, settings) is { } file)
                {
                    await UpsertAsync([file], now, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await _store.RemoveSourceAsync(itemId, now, cancellationToken).ConfigureAwait(false);
                }

                // A newer event replaces the one that failed.
                _failedChanges.Remove(itemId);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _failedChanges[itemId] = exists;
                _logger.LogWarning(ex, "Recording item {ItemId} for HLS failed", itemId);
            }
        }

        await _index.RefreshItemsAsync(processed, cancellationToken).ConfigureAwait(false);
    }

    // A changed or new file gets a source job from the store; a known content only needs scheduling.
    private async Task UpsertAsync(IReadOnlyList<HlsSourceFile> files, DateTime now, CancellationToken cancellationToken)
    {
        foreach (var result in await _store.UpsertSourcesAsync(files, now, cancellationToken).ConfigureAwait(false))
        {
            if (result is { Change: HlsSourceChange.Unchanged, PreviousSha256: { } sha })
            {
                _toSchedule[sha] = 0;
            }
        }
    }

    private async Task RemoveSourceAsync(Guid itemId, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            await _store.RemoveSourceAsync(itemId, now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _failedChanges[itemId] = false;
            _logger.LogWarning(ex, "Removing item {ItemId} from HLS failed", itemId);
        }
    }

    // The file of a video in an enabled library: a local file, or the target of a .strm file that the network storage can read.
    // A .strm file has the size TrueCache found for its target and its own modification time. Null when the video is not one,
    // or its size is not known.
    private HlsSourceFile? GetSourceFile(Video video, HlsSettingsSnapshot settings)
    {
        if (video.VideoType != VideoType.VideoFile)
        {
            return null;
        }

        // A folder can belong to several libraries: any of them that is selected will do.
        var libraryId = _libraryManager.GetCollectionFolders(video).Select(f => f.Id).FirstOrDefault(settings.IsLibraryEligible);
        if (libraryId.Equals(Guid.Empty))
        {
            return null;
        }

        string path;
        long length;
        if (video.IsShortcut)
        {
            if (string.IsNullOrEmpty(video.ShortcutPath) || !IsHttpAddress(_networkStorage.ResolveShortcutTarget(video.ShortcutPath)))
            {
                return null;
            }

            // Only the entry declares the size of the target; without it the item has the size of the text file.
            var lookup = _trueCache.Lookup(video.Path);
            path = video.ShortcutPath;
            length = lookup.IsValid && lookup.Entry.MediaSize is { } size ? size : -1;
        }
        else if (video.IsFileProtocol && !string.IsNullOrEmpty(video.Path))
        {
            path = video.Path;
            length = video.Size ?? (File.Exists(path) ? new FileInfo(path).Length : -1);
        }
        else
        {
            return null;
        }

        return length < 0 ? null : new HlsSourceFile(video.Id, libraryId, path, length, ToUniversalTime(video.DateModified));
    }

    private async Task ScheduleQueuedAsync(HlsSettingsSnapshot settings, DateTime now, CancellationToken cancellationToken)
    {
        foreach (var sha in _toSchedule.Keys)
        {
            if (_toSchedule.TryRemove(sha, out _))
            {
                await ScheduleAsync(sha, settings, now, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // The job reads the file of an item the index knows for the content, when there is one.
    private async Task ScheduleAsync(string sha256, HlsSettingsSnapshot settings, DateTime now, CancellationToken cancellationToken)
    {
        try
        {
            var items = _index.GetItemsForSha(sha256);
            Guid? preferredItem = items.Count == 0 ? null : items.Min();
            await _store.EnsureMediaJobAsync(sha256, preferredItem, HlsEncodingProfile.Version, settings.IsLibraryEligible, now, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Scheduling {Sha256} for HLS failed", sha256);
        }
    }

    // Files whose content changed while their source job ran, contents whose renditions lost their job, and what failed since
    // the last sweep: the changes go back to the queue of the next tick, where newer events win.
    private async Task SweepAsync(HlsSettingsSnapshot settings, DateTime now, CancellationToken cancellationToken)
    {
        foreach (var (itemId, exists) in _failedChanges)
        {
            _changes.TryAdd(itemId, exists);
        }

        _failedChanges.Clear();
        foreach (var sha in _unsynced.ToList())
        {
            await SyncRenditionsAsync(sha, settings, now, cancellationToken).ConfigureAwait(false);
        }

        await _store.EnsureSourceJobsAsync(now, cancellationToken).ConfigureAwait(false);
        foreach (var sha in await _store.GetShasNeedingJobsAsync(HlsEncodingProfile.Version, cancellationToken).ConfigureAwait(false))
        {
            await ScheduleAsync(sha, settings, now, cancellationToken).ConfigureAwait(false);
        }

        _nextSweep = now + _sweepInterval;
    }

    private async Task CollectOrphansAsync(HlsSettingsSnapshot settings, DateTime now, CancellationToken cancellationToken)
    {
        await _store.RefreshOrphansAsync(now, cancellationToken).ConfigureAwait(false);
        var deleted = await _store.DeleteExpiredOrphansAsync(now - TimeSpan.FromHours(settings.Options.OrphanRetentionHours), now, cancellationToken).ConfigureAwait(false);
        await _index.RefreshMediaAsync(deleted, cancellationToken).ConfigureAwait(false);
        _nextOrphanRun = now + _sweepInterval;
    }

    // The arrays are sorted, so a list in another order is no change.
    private sealed record AppliedSettings(int Version, bool Enabled, Guid[] Libraries, int[] Qualities, string Store);
}
