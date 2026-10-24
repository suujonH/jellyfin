using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Model.Hls;
using MediaBrowser.Model.NetworkStorage;
using MediaBrowser.Model.Querying;
using Microsoft.EntityFrameworkCore;

namespace Jellyfin.Server.Implementations.NetworkStorage;

/// <summary>
/// What the library has in each network storage: the sources of the <c>.strm</c> items the network storage knows, and the
/// pre-encoded HLS of every content, with the items it is the content of and the libraries of those.
/// </summary>
public sealed class NetworkStorageInventory : INetworkStorageInventory
{
    private const string OrphansGroup = "orphans";
    private const string LibraryGroupPrefix = "library:";
    private const string SeriesGroupPrefix = "series:";
    private const string NoLibrary = "none";

    private readonly INetworkStorageManager _networkStorage;
    private readonly HlsJobStore _store;
    private readonly HlsSettings _settings;
    private readonly ILibraryManager _libraryManager;
    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkStorageInventory"/> class.
    /// </summary>
    /// <param name="networkStorage">The network storage manager.</param>
    /// <param name="store">The store of the pre-encoded HLS.</param>
    /// <param name="settings">The HLS settings.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="dbProvider">The database context factory.</param>
    public NetworkStorageInventory(
        INetworkStorageManager networkStorage,
        HlsJobStore store,
        HlsSettings settings,
        ILibraryManager libraryManager,
        IDbContextFactory<JellyfinDbContext> dbProvider)
    {
        _networkStorage = networkStorage;
        _store = store;
        _settings = settings;
        _libraryManager = libraryManager;
        _dbProvider = dbProvider;
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <inheritdoc />
    public async Task<IReadOnlyList<NetworkStorageSummary>> GetStoragesAsync(CancellationToken cancellationToken)
    {
        var inventory = await BuildAsync(cancellationToken).ConfigureAwait(false);
        var summaries = new Dictionary<string, NetworkStorageSummary>(StringComparer.Ordinal);

        // Every access is listed, with or without resources.
        foreach (var access in _networkStorage.GetAccesses())
        {
            var key = StorageLocation.GetStorageKey(access.Protocol, access.Bucket);
            summaries[key] = new NetworkStorageSummary
            {
                Key = key,
                Protocol = access.Protocol,
                Name = access.Bucket,
                AccessId = access.Id,
                IsArchive = access.IsArchive,
                RequiresAccess = true
            };
        }

        foreach (var (key, storage) in inventory.Storages)
        {
            if (!summaries.TryGetValue(key, out var summary))
            {
                var entry = inventory.Entries.FirstOrDefault(e => string.Equals(e.StorageKey, key, StringComparison.Ordinal));
                var parts = key.Split(':', 2);
                summary = new NetworkStorageSummary
                {
                    Key = key,
                    Protocol = entry?.Protocol ?? (parts.Length == 2 ? parts[0] : string.Empty),
                    Name = entry?.Container ?? (parts.Length == 2 ? parts[1] : string.Empty),
                    RequiresAccess = entry?.RequiresAccess ?? false
                };
                summaries[key] = summary;
            }

            var totals = Accumulate(storage);
            summary.ResourceCount = totals.ResourceCount;
            summary.ValidCount = totals.ValidCount;
            summary.InvalidCount = totals.InvalidCount;
            summary.UnknownCount = totals.UnknownCount;
            summary.UndeclaredSizeCount = totals.UndeclaredSizeCount;
            summary.SourceSize = totals.SourceSize;
            summary.HlsSize = totals.HlsSize;
            summary.TotalSize = totals.TotalSize;
        }

        return summaries.Values
            .OrderBy(s => s.Key == NetworkStorageManager.UnresolvedStorageKey)
            .ThenBy(s => s.Protocol, StringComparer.Ordinal)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<NetworkStorageGroup>> GetGroupsAsync(string storageKey, IReadOnlyCollection<NetworkStorageResourceState> states, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(states);
        var inventory = await BuildAsync(cancellationToken).ConfigureAwait(false);
        if (!inventory.Storages.TryGetValue(storageKey, out var storage))
        {
            return [];
        }

        var rows = Filter(storage, states);
        var groups = new List<NetworkStorageGroup>();
        foreach (var library in rows.Where(r => !r.IsOrphan).GroupBy(r => r.LibraryId))
        {
            var libraryRows = library.ToList();
            var group = Accumulate(libraryRows);
            group.Key = LibraryGroupPrefix + (library.Key?.ToString("N") ?? NoLibrary);
            group.Kind = NetworkStorageGroupKind.Library;
            group.LibraryId = library.Key;
            group.Name = libraryRows[0].LibraryName;
            group.LooseCount = libraryRows.Count(r => r.SeriesId is null);
            group.Children = libraryRows
                .Where(r => r.SeriesId is not null)
                .GroupBy(r => r.SeriesId!.Value)
                .Select(series =>
                {
                    var seriesRows = series.ToList();
                    var child = Accumulate(seriesRows);
                    child.Key = SeriesGroupPrefix + series.Key.ToString("N");
                    child.Kind = NetworkStorageGroupKind.Series;
                    child.LibraryId = library.Key;
                    child.SeriesId = series.Key;
                    child.Name = seriesRows[0].SeriesName;
                    return child;
                })
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            groups.Add(group);
        }

        var ordered = groups
            .OrderBy(g => g.LibraryId is null)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var orphans = rows.Where(r => r.IsOrphan).ToList();
        if (orphans.Count > 0)
        {
            var group = Accumulate(orphans);
            group.Key = OrphansGroup;
            group.Kind = NetworkStorageGroupKind.Orphans;
            ordered.Add(group);
        }

        return ordered;
    }

    /// <inheritdoc />
    public async Task<QueryResult<NetworkStorageResource>> GetResourcesAsync(
        string storageKey,
        IReadOnlyCollection<NetworkStorageResourceState> states,
        string? group,
        int startIndex,
        int? limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(states);
        var inventory = await BuildAsync(cancellationToken).ConfigureAwait(false);
        var rows = inventory.Storages.TryGetValue(storageKey, out var storage) ? Filter(storage, states) : [];

        NetworkStorageResource[] matching;
        if (group is null)
        {
            matching = rows
                .OrderBy(r => r.State switch
                {
                    NetworkStorageResourceState.Invalid => 0,
                    NetworkStorageResourceState.Unknown => 1,
                    _ => 2
                })
                .ThenBy(r => r.SeriesName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
        }
        else
        {
            var inGroup = GetGroupFilter(group);
            matching = inGroup is null
                ? []
                : rows
                    .Where(inGroup)
                    .OrderBy(r => r.SeasonNumber ?? int.MaxValue)
                    .ThenBy(r => r.EpisodeNumber ?? int.MaxValue)
                    .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray();
        }

        IEnumerable<NetworkStorageResource> page = matching.Skip(Math.Max(0, startIndex));
        if (limit.HasValue)
        {
            page = page.Take(Math.Max(0, limit.Value));
        }

        return new QueryResult<NetworkStorageResource>(startIndex, matching.Length, page.ToArray());
    }

    // Which resources a group key lists: the orphans, the items of a library in no series, or the items of a series. Null for
    // a key that is not one.
    private static Func<NetworkStorageResource, bool>? GetGroupFilter(string group)
    {
        if (string.Equals(group, OrphansGroup, StringComparison.Ordinal))
        {
            return r => r.IsOrphan;
        }

        if (group.StartsWith(LibraryGroupPrefix, StringComparison.Ordinal))
        {
            var id = group[LibraryGroupPrefix.Length..];
            if (string.Equals(id, NoLibrary, StringComparison.Ordinal))
            {
                return r => !r.IsOrphan && r.LibraryId is null && r.SeriesId is null;
            }

            return Guid.TryParseExact(id, "N", out var libraryId)
                ? r => !r.IsOrphan && r.LibraryId == libraryId && r.SeriesId is null
                : null;
        }

        if (group.StartsWith(SeriesGroupPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(group[SeriesGroupPrefix.Length..], "N", out var seriesId))
        {
            return r => r.SeriesId == seriesId;
        }

        return null;
    }

    private static List<NetworkStorageResource> Filter(List<NetworkStorageResource> rows, IReadOnlyCollection<NetworkStorageResourceState> states)
        => states.Count == 0 ? rows : rows.Where(r => states.Contains(r.State)).ToList();

    // The counts and sizes of rows: the sources in the storage row by row, the HLS once for each content, since items can
    // share one.
    private static NetworkStorageGroup Accumulate(IReadOnlyList<NetworkStorageResource> rows)
    {
        var group = new NetworkStorageGroup { ResourceCount = rows.Count };
        foreach (var row in rows)
        {
            switch (row.State)
            {
                case NetworkStorageResourceState.Valid:
                    group.ValidCount++;
                    break;
                case NetworkStorageResourceState.Invalid:
                    group.InvalidCount++;
                    break;
                default:
                    group.UnknownCount++;
                    break;
            }

            if (!row.SourceInStorage)
            {
                continue;
            }

            if (row.Size is { } size)
            {
                group.SourceSize += size;
            }
            else
            {
                group.UndeclaredSizeCount++;
            }
        }

        group.HlsSize = rows
            .Where(r => r.Sha256 is not null)
            .DistinctBy(r => r.Sha256, StringComparer.Ordinal)
            .Sum(r => r.HlsSize);
        group.TotalSize = group.SourceSize + group.HlsSize;
        return group;
    }

    // The resource of an item in one storage: its source counts only when it is in that storage, and its state is that of
    // the source then; otherwise the item is there by its published HLS, which is known to be there.
    private static NetworkStorageResource MakeRow(Source source, string storageKey, HlsStoredContent? content, IReadOnlyDictionary<string, string?> rootKeys)
    {
        var resource = source.Resource;
        var sourceInStorage = source.Entry is not null && string.Equals(source.Entry.StorageKey, storageKey, StringComparison.Ordinal);
        var renditions = (content?.Renditions ?? [])
            .Select(r => new NetworkStorageHlsRendition
            {
                Key = r.Key,
                Kind = r.Kind,
                Quality = r.Kind == HlsRenditionKind.Video ? r.Summary.Quality : 0,
                Language = r.Summary.Language,
                Title = r.Summary.Title,
                Channels = r.Summary.Channels,
                Size = r.Size,
                FileCount = r.FileCount,
                Location = r.Base,
                StorageKey = rootKeys[r.Base],
                InStorage = string.Equals(rootKeys[r.Base], storageKey, StringComparison.Ordinal)
            })
            .ToArray();
        var hlsSize = renditions.Where(r => r.InStorage).Sum(r => r.Size);
        return new NetworkStorageResource
        {
            ItemId = resource.ItemId,
            Name = resource.Name,
            SeriesName = resource.SeriesName,
            Type = resource.Type,
            Path = resource.Path,
            Target = resource.Target,
            Size = resource.Size,
            HasTrueCache = resource.HasTrueCache,
            State = sourceInStorage ? resource.State : NetworkStorageResourceState.Valid,
            Issue = sourceInStorage ? resource.Issue : null,
            StatusCode = sourceInStorage ? resource.StatusCode : null,
            FailureSource = sourceInStorage ? resource.FailureSource : null,
            DateLastFailed = sourceInStorage ? resource.DateLastFailed : null,
            FailureCount = sourceInStorage ? resource.FailureCount : 0,
            LibraryId = source.LibraryId,
            LibraryName = source.LibraryName,
            SeriesId = resource.SeriesId,
            SeasonNumber = resource.SeasonNumber,
            EpisodeNumber = resource.EpisodeNumber,
            Sha256 = content?.Sha256,
            SourceInStorage = sourceInStorage,
            SourceStorageKey = source.Entry?.StorageKey,
            HlsSize = hlsSize,
            TotalSize = hlsSize + (sourceInStorage ? resource.Size ?? 0 : 0),
            HlsRenditions = renditions
        };
    }

    // The rows of every storage, each its own object, since one item is a row of each storage it has a part in.
    private async Task<Inventory> BuildAsync(CancellationToken cancellationToken)
    {
        var entries = await _networkStorage.GetResourceEntriesAsync(cancellationToken).ConfigureAwait(false);
        var contents = await _store.GetStoredContentsAsync(cancellationToken).ConfigureAwait(false);

        var contentByItem = new Dictionary<Guid, HlsStoredContent>();
        foreach (var content in contents)
        {
            foreach (var itemId in content.ItemIds)
            {
                contentByItem[itemId] = content;
            }
        }

        var rootKeys = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var rendition in contents.SelectMany(c => c.Renditions))
        {
            if (!rootKeys.ContainsKey(rendition.Base))
            {
                rootKeys[rendition.Base] = _networkStorage.GetStorageKey(rendition.Base);
            }
        }

        var sources = entries.Select(e => new Source(e.Resource, e, e.TopParentId)).ToList();
        var listed = entries.Select(e => e.Resource.ItemId).ToHashSet();
        var others = contentByItem.Keys.Where(id => !listed.Contains(id)).ToList();

        var context = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            if (others.Count > 0)
            {
                // Items that are not .strm items of a network storage have their source in none.
                var items = await context.BaseItems.AsNoTracking()
                    .Where(e => others.Contains(e.Id))
                    .Select(e => new { e.Id, e.Name, e.SeriesName, e.SeriesId, e.Type, e.Path, e.Size, e.TopParentId, e.IndexNumber, e.ParentIndexNumber })
                    .ToListAsync(cancellationToken)
                    .ConfigureAwait(false);
                sources.AddRange(items.Select(e => new Source(
                    new NetworkStorageResource
                    {
                        ItemId = e.Id,
                        Name = e.Name,
                        SeriesName = e.SeriesName,
                        SeriesId = e.SeriesId,
                        Type = e.Type[(e.Type.LastIndexOf('.') + 1)..],
                        Path = e.Path ?? string.Empty,
                        Size = e.Size,
                        State = NetworkStorageResourceState.Valid,
                        SeasonNumber = e.ParentIndexNumber,
                        EpisodeNumber = e.IndexNumber
                    },
                    null,
                    e.TopParentId)));
            }

            var topParentIds = sources.Where(s => s.TopParentId.HasValue).Select(s => s.TopParentId!.Value).Distinct().ToList();
            var topParents = await context.BaseItems.AsNoTracking()
                .Where(e => topParentIds.Contains(e.Id))
                .Select(e => new { e.Id, e.Path })
                .ToDictionaryAsync(e => e.Id, e => e.Path, cancellationToken)
                .ConfigureAwait(false);
            var libraries = GetLibraries(topParents);
            foreach (var source in sources)
            {
                if (source.TopParentId is { } topParentId && libraries.TryGetValue(topParentId, out var library))
                {
                    source.LibraryId = library.Id;
                    source.LibraryName = library.Name;
                }
            }
        }

        var storages = new Dictionary<string, List<NetworkStorageResource>>(StringComparer.Ordinal);
        void Add(string key, NetworkStorageResource row)
        {
            if (!storages.TryGetValue(key, out var rows))
            {
                rows = [];
                storages[key] = rows;
            }

            rows.Add(row);
        }

        foreach (var source in sources)
        {
            var content = contentByItem.GetValueOrDefault(source.Resource.ItemId);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            if (source.Entry is not null)
            {
                keys.Add(source.Entry.StorageKey);
            }

            foreach (var rendition in content?.Renditions ?? [])
            {
                if (rootKeys[rendition.Base] is { } key)
                {
                    keys.Add(key);
                }
            }

            foreach (var key in keys)
            {
                Add(key, MakeRow(source, key, content, rootKeys));
            }
        }

        foreach (var content in contents.Where(c => c.ItemIds.Count == 0))
        {
            var retention = TimeSpan.FromHours(_settings.Current.Options.OrphanRetentionHours);
            var orphan = new Source(new NetworkStorageResource { State = NetworkStorageResourceState.Valid }, null, null) { LibraryId = content.LibraryId };
            foreach (var key in content.Renditions.Select(r => rootKeys[r.Base]).OfType<string>().Distinct(StringComparer.Ordinal))
            {
                var row = MakeRow(orphan, key, content, rootKeys);
                row.ItemId = Guid.Empty;
                row.IsOrphan = true;
                row.DateOrphanExpires = content.DateOrphaned + retention;
                Add(key, row);
            }
        }

        return new Inventory(entries, storages);
    }

    // The library of each top folder: the virtual folder one of whose locations it is.
    private Dictionary<Guid, (Guid Id, string Name)> GetLibraries(Dictionary<Guid, string?> topParents)
    {
        var folders = _libraryManager.GetVirtualFolders()
            .Where(f => Guid.TryParse(f.ItemId, out _))
            .Select(f => (Id: Guid.Parse(f.ItemId), f.Name, Locations: f.Locations.Select(Path.TrimEndingDirectorySeparator).ToArray()))
            .ToList();
        var libraries = new Dictionary<Guid, (Guid Id, string Name)>();
        foreach (var (id, path) in topParents)
        {
            var folder = folders.FirstOrDefault(f => f.Id == id
                || (path is not null && f.Locations.Any(l => string.Equals(l, Path.TrimEndingDirectorySeparator(path), PathComparison))));
            if (folder.Name is not null)
            {
                libraries[id] = (folder.Id, folder.Name);
            }
        }

        return libraries;
    }

    private sealed record Inventory(IReadOnlyList<NetworkStorageResourceEntry> Entries, Dictionary<string, List<NetworkStorageResource>> Storages);

    // An item and where its source is: the entry of the network storage for a .strm item, none otherwise.
    private sealed record Source(NetworkStorageResource Resource, NetworkStorageResourceEntry? Entry, Guid? TopParentId)
    {
        public Guid? LibraryId { get; set; }

        public string? LibraryName { get; set; }
    }
}
