using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// Keeps one real Video item per published quality of an item, plus one for "auto", next to the item: a version item. It only
/// carries <see cref="Video.PrimaryVersionId"/>, no owner and no link from the original, so user data stays on the original, and
/// it is never part of the sources of the original.
/// </summary>
/// <remarks>
/// Every five seconds it syncs the items the published index or the library changed, and every item when the settings change.
/// Nothing is synced before the published index is loaded, so a version item is never deleted for want of it.
/// </remarks>
public sealed class HlsVersionCatalog : BackgroundService
{
    /// <summary>
    /// The provider id that marks a version item, valued <c>{original:N}:{quality}</c>.
    /// </summary>
    public const string Marker = "DistributedHls";

    private const int BatchSize = 200;

    private static readonly TimeSpan _tickInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan _retryInterval = TimeSpan.FromMinutes(10);

    private readonly HlsJobStore _store;
    private readonly HlsPublishedIndex _index;
    private readonly HlsSettings _settings;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaStreamRepository _mediaStreams;
    private readonly ILogger<HlsVersionCatalog> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _dirty = new();

    // Only the loop uses these: the stored version items by original and quality, and the items whose sync failed.
    private Dictionary<Guid, Dictionary<int, HlsVersionItem>>? _rows;
    private readonly HashSet<Guid> _failed = [];
    private int? _appliedVersion;
    private DateTime _nextRetry;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsVersionCatalog"/> class.
    /// </summary>
    /// <param name="store">The data of pre-encoded HLS.</param>
    /// <param name="index">The published index.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="mediaStreams">The media stream repository.</param>
    /// <param name="logger">The logger.</param>
    public HlsVersionCatalog(
        HlsJobStore store,
        HlsPublishedIndex index,
        HlsSettings settings,
        ILibraryManager libraryManager,
        IMediaStreamRepository mediaStreams,
        ILogger<HlsVersionCatalog> logger)
    {
        _store = store;
        _index = index;
        _settings = settings;
        _libraryManager = libraryManager;
        _mediaStreams = mediaStreams;
        _logger = logger;
        _index.Changed += OnIndexChanged;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _libraryManager.ItemRemoved += OnItemRemoved;
    }

    /// <summary>
    /// Reads the marker of a version item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>The original and the quality, or <c>null</c> when the item is not a version item.</returns>
    public static (Guid ItemId, int Quality)? Identify(BaseItem? item)
    {
        if (item is null || !item.ProviderIds.TryGetValue(Marker, out var value))
        {
            return null;
        }

        // A third segment, which older version items carry, is ignored.
        var parts = value.Split(':');
        return parts.Length >= 2
            && Guid.TryParse(parts[0], out var itemId)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var quality)
                ? (itemId, quality)
                : null;
    }

    /// <summary>
    /// Gets whether an item is a version item.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Whether it carries the marker.</returns>
    public static bool IsVersionItem(BaseItem item) => item.ProviderIds.ContainsKey(Marker);

    /// <summary>
    /// Gets the id of the version item of a quality: the id the DistributedHls plugin gave it, so its items are kept.
    /// </summary>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="itemId">The original.</param>
    /// <param name="quality">The quality, or 0 for "auto".</param>
    /// <returns>The id.</returns>
    public static Guid GetVersionId(ILibraryManager libraryManager, Guid itemId, int quality)
        => libraryManager.GetNewItemId(GetVersionPath(itemId, quality), typeof(Video));

    /// <inheritdoc />
    public override void Dispose()
    {
        _index.Changed -= OnIndexChanged;
        _libraryManager.ItemUpdated -= OnItemUpdated;
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
        if (!_index.IsLoaded)
        {
            return;
        }

        if (_rows is null)
        {
            try
            {
                _rows = (await _store.GetVersionItemsAsync(cancellationToken).ConfigureAwait(false))
                    .GroupBy(r => r.ItemId)
                    .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.Quality));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Failed to read the HLS version items");
                return;
            }
        }

        // The first round, and every change of the settings, syncs every item: the libraries or qualities may have changed.
        var settings = _settings.Current;
        if (_appliedVersion != settings.Version)
        {
            _appliedVersion = settings.Version;
            MarkDirty(_index.GetPublishedItems());
            MarkDirty(_rows.Keys);
        }

        if (now >= _nextRetry)
        {
            _nextRetry = now + _retryInterval;
            MarkDirty(_failed);
            _failed.Clear();
        }

        foreach (var itemId in _dirty.Keys.Take(BatchSize).ToList())
        {
            if (!_dirty.TryRemove(itemId, out _))
            {
                continue;
            }

            try
            {
                await SyncItemAsync(itemId, settings, _rows, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _failed.Add(itemId);
                _logger.LogError(ex, "Failed to sync the HLS version items of {ItemId}", itemId);
            }
        }
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            using var timer = new PeriodicTimer(_tickInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                await TickAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // An exception leaving a BackgroundService stops the whole server.
            _logger.LogCritical(ex, "The HLS version catalog stopped unexpectedly");
        }
    }

    private static string GetVersionPath(Guid itemId, int quality)
        => string.Create(CultureInfo.InvariantCulture, $"https://distributed-hls.invalid/{itemId:N}/{quality}/master.m3u8");

    // Sets every field a version item takes from its original and what is published; returns whether any value differed before.
    private static bool ApplyFields(Video version, Video original, HlsMediaView media, int quality)
    {
        var parent = original.GetParent() as Folder;
        var video = HlsPlaybackService.GetVideo(media, quality);
        var ticks = TimeSpan.FromSeconds(media.DurationSeconds).Ticks;
        var marker = string.Create(CultureInfo.InvariantCulture, $"{original.Id:N}:{quality}");
        var differs = !string.Equals(version.Name, original.Name, StringComparison.Ordinal)
            || !string.Equals(version.ForcedSortName, original.SortName, StringComparison.Ordinal)
            || !version.ParentId.Equals(parent?.Id ?? Guid.Empty)
            || version.PrimaryVersionId != original.Id
            || !version.OwnerId.Equals(Guid.Empty)
            || version.RunTimeTicks != ticks
            || !string.Equals(version.Container, "hls", StringComparison.Ordinal)
            || !version.IsLocked
            || !string.Equals(version.OfficialRating, original.OfficialRating, StringComparison.Ordinal)
            || !string.Equals(version.CustomRating, original.CustomRating, StringComparison.Ordinal)
            || !version.Tags.SequenceEqual(original.Tags, StringComparer.Ordinal)
            || !GetImages(version.ImageInfos).SequenceEqual(GetImages(original.ImageInfos))
            || !string.Equals(version.Overview, original.Overview, StringComparison.Ordinal)
            || version.ProductionYear != original.ProductionYear
            || version.PremiereDate != original.PremiereDate
            || version.CommunityRating != original.CommunityRating
            || version.Width != video.Width
            || version.Height != video.Height
            || version.ProviderIds.Count != 1
            || !version.ProviderIds.TryGetValue(Marker, out var current)
            || !string.Equals(current, marker, StringComparison.Ordinal);
        version.Name = original.Name;
        version.ForcedSortName = original.SortName;
        version.SetParent(parent);
        version.SetPrimaryVersionId(original.Id);
        version.OwnerId = Guid.Empty;
        version.RunTimeTicks = ticks;
        version.Container = "hls";
        version.IsLocked = true;
        version.OfficialRating = original.OfficialRating;
        version.CustomRating = original.CustomRating;
        version.Tags = [.. original.Tags];
        version.ImageInfos =
        [
            .. original.ImageInfos.Select(i => new ItemImageInfo
            {
                Path = i.Path,
                Type = i.Type,
                DateModified = i.DateModified,
                Width = i.Width,
                Height = i.Height,
                BlurHash = i.BlurHash
            })
        ];
        version.Overview = original.Overview;
        version.ProductionYear = original.ProductionYear;
        version.PremiereDate = original.PremiereDate;
        version.CommunityRating = original.CommunityRating;
        version.Width = video.Width;
        version.Height = video.Height;
        // Only the marker: provider ids such as Imdb or Tmdb are never copied, so the version item is never matched as the film.
        version.ProviderIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Marker] = marker };
        return differs;
    }

    // The stored order of the images is not kept, so they are compared sorted.
    private static IEnumerable<(string Path, ImageType Type, DateTime DateModified, int Width, int Height, string? BlurHash)> GetImages(ItemImageInfo[] images)
        => images.Select(i => (i.Path, i.Type, i.DateModified, i.Width, i.Height, i.BlurHash))
            .OrderBy(i => i.Type)
            .ThenBy(i => i.Path, StringComparer.Ordinal);

    private static string GetKeys(IEnumerable<HlsPublishedRendition> renditions) => string.Join(',', renditions.Select(r => r.Key));

    private void MarkDirty(IEnumerable<Guid> itemIds)
    {
        foreach (var itemId in itemIds)
        {
            _dirty[itemId] = 0;
        }
    }

    private void OnIndexChanged(object? sender, IReadOnlyCollection<Guid> itemIds) => MarkDirty(itemIds);

    // The metadata of an original is copied to its version items.
    private void OnItemUpdated(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Video item && !IsVersionItem(item) && _index.GetSource(item.Id) is not null)
        {
            _dirty[item.Id] = 0;
        }
    }

    // A deleted original takes its version items with it.
    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        if (e.Item is Video item && !IsVersionItem(item))
        {
            _dirty[item.Id] = 0;
        }
    }

    // A deleted original arrives here too, with nothing desired, so its version items are deleted by id.
    private async Task SyncItemAsync(Guid itemId, HlsSettingsSnapshot settings, Dictionary<Guid, Dictionary<int, HlsVersionItem>> rows, CancellationToken cancellationToken)
    {
        var original = _libraryManager.GetItemById(itemId) as Video;
        if (!rows.TryGetValue(itemId, out var existing))
        {
            existing = [];
            rows[itemId] = existing;
        }

        var desired = new HashSet<int>();
        if (original is not null && !IsVersionItem(original) && HlsPlaybackService.GetPlayableMedia(_index, settings, itemId) is { } media)
        {
            var signature = string.Join(
                '|',
                HlsEncodingProfile.Version,
                media.Sha256,
                GetKeys(media.Video),
                GetKeys(media.Audio),
                GetKeys(media.Subtitles),
                original.DateModified.Ticks.ToString(CultureInfo.InvariantCulture),
                original.ImageInfos.Length.ToString(CultureInfo.InvariantCulture));
            IReadOnlyList<MediaStream>? originalStreams = null;
            foreach (var quality in media.Video.Select(v => v.Summary.Quality).Prepend(0))
            {
                desired.Add(quality);
                var id = GetVersionId(_libraryManager, itemId, quality);

                // Recorded first, without what it is written from, so a sync that fails halfway still finds it to delete or retry.
                if (!existing.ContainsKey(quality))
                {
                    await _store.SetVersionItemAsync(itemId, quality, id, string.Empty, cancellationToken).ConfigureAwait(false);
                    existing[quality] = new HlsVersionItem { ItemId = itemId, Quality = quality, VersionId = id, Signature = string.Empty };
                }

                if (_libraryManager.GetItemById(id) is not Video version)
                {
                    version = new Video { Id = id, Path = GetVersionPath(itemId, quality) };
                    ApplyFields(version, original, media, quality);
                    _libraryManager.CreateItem(version, original.GetParent());
                }
                else
                {
                    var differs = ApplyFields(version, original, media, quality);
                    if (!differs && existing.TryGetValue(quality, out var stored) && string.Equals(stored.Signature, signature, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // The fields are already applied to the cached item: until the write succeeds, nothing marks it as written.
                    existing[quality] = new HlsVersionItem { ItemId = itemId, Quality = quality, VersionId = id, Signature = string.Empty };
                    await _libraryManager.UpdateItemAsync(version, version.GetParent(), ItemUpdateType.MetadataEdit, cancellationToken).ConfigureAwait(false);
                }

                originalStreams ??= _mediaStreams.GetMediaStreams(new MediaStreamQuery { ItemId = itemId });
                _mediaStreams.SaveMediaStreams(id, HlsPlaybackService.BuildStreams(media, quality, originalStreams), cancellationToken);
                await _store.SetVersionItemAsync(itemId, quality, id, signature, cancellationToken).ConfigureAwait(false);
                existing[quality] = new HlsVersionItem { ItemId = itemId, Quality = quality, VersionId = id, Signature = signature };
            }
        }

        foreach (var (quality, row) in existing.Where(p => !desired.Contains(p.Key)).ToList())
        {
            if (_libraryManager.GetItemById(row.VersionId) is { } version && IsVersionItem(version))
            {
                // As an alternate version it would list the sources of its original, whose extracted subtitles and attachments
                // would be deleted with it, and write the original again.
                ((Video)version).SetPrimaryVersionId(null);
                _libraryManager.DeleteItem(version, new DeleteOptions { DeleteFileLocation = false });
            }

            await _store.RemoveVersionItemAsync(itemId, quality, cancellationToken).ConfigureAwait(false);
            existing.Remove(quality);
        }

        if (existing.Count == 0)
        {
            rows.Remove(itemId);
        }
    }
}
