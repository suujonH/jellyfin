using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Model.Hls;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// The library files and what is published of their contents, in memory, for playback, which never reads the database.
/// Whoever commits a change to sources, contents or renditions refreshes the matching part afterwards. Writers build the next
/// snapshot under one lock and read the database inside it, so the last refresh sees every earlier commit; readers never lock.
/// A refresh that fails leaves its part to the next one, which the coordinator asks for on every tick. Every refresh raises
/// <see cref="Changed"/> with the items it may have changed.
/// </summary>
public sealed class HlsPublishedIndex : IDisposable
{
    private static readonly Snapshot _empty = new(
        ImmutableDictionary<Guid, HlsIndexedSource>.Empty,
        ImmutableDictionary.Create<string, HlsMediaView>(StringComparer.Ordinal),
        ImmutableDictionary.Create<string, ImmutableHashSet<Guid>>(StringComparer.Ordinal));

    private readonly HlsJobStore _store;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    // What failed refreshes left out, under the lock.
    private readonly HashSet<Guid> _staleItems = [];
    private readonly HashSet<string> _staleMedia = new(StringComparer.Ordinal);
    private Snapshot _snapshot = _empty;
    private bool _loaded;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsPublishedIndex"/> class.
    /// </summary>
    /// <param name="store">The data of pre-encoded HLS.</param>
    public HlsPublishedIndex(HlsJobStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Raised after a refresh, outside the lock, with the items whose files or published renditions it may have changed.
    /// </summary>
    public event EventHandler<IReadOnlyCollection<Guid>>? Changed;

    /// <summary>
    /// Gets a value indicating whether everything was read once.
    /// </summary>
    public bool IsLoaded => Volatile.Read(ref _loaded);

    /// <inheritdoc />
    public void Dispose() => _writeLock.Dispose();

    /// <summary>
    /// Reads everything again.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IReadOnlyCollection<Guid> affected;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var items = (await _store.GetSourcesAsync(cancellationToken).ConfigureAwait(false)).Select(ToIndexed).ToImmutableDictionary(s => s.ItemId);
            var shas = items.Values
                .Where(s => s.Sha256 is not null)
                .GroupBy(s => s.Sha256!, StringComparer.Ordinal)
                .ToImmutableDictionary(g => g.Key, g => g.Select(s => s.ItemId).ToImmutableHashSet(), StringComparer.Ordinal);
            var media = ImmutableDictionary.CreateBuilder<string, HlsMediaView>(StringComparer.Ordinal);
            foreach (var renditions in (await _store.GetPublishedRenditionsAsync(cancellationToken).ConfigureAwait(false)).GroupBy(r => r.Sha256, StringComparer.Ordinal))
            {
                if (GetView(await _store.GetMediaAsync(renditions.Key, cancellationToken).ConfigureAwait(false), renditions) is { } view)
                {
                    media[renditions.Key] = view;
                }
            }

            affected = [.. Volatile.Read(ref _snapshot).Items.Keys.Union(items.Keys)];
            Volatile.Write(ref _snapshot, new Snapshot(items, media.ToImmutable(), shas));
            Volatile.Write(ref _loaded, true);
            _staleItems.Clear();
            _staleMedia.Clear();
        }
        finally
        {
            _writeLock.Release();
        }

        Raise(affected);
    }

    /// <summary>
    /// Reads library files again.
    /// </summary>
    /// <param name="itemIds">The items.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RefreshItemsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
    {
        var requested = itemIds.ToList();
        List<Guid> list;
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            list = requested.Concat(_staleItems).Distinct().ToList();
            if (list.Count == 0)
            {
                return;
            }

            try
            {
                var current = Volatile.Read(ref _snapshot);
                var items = current.Items.ToBuilder();
                var shas = current.ShaToItems.ToBuilder();
                foreach (var itemId in list)
                {
                    if (items.TryGetValue(itemId, out var old))
                    {
                        if (old.Sha256 is { } oldSha && shas.TryGetValue(oldSha, out var members))
                        {
                            members = members.Remove(itemId);
                            if (members.IsEmpty)
                            {
                                shas.Remove(oldSha);
                            }
                            else
                            {
                                shas[oldSha] = members;
                            }
                        }

                        items.Remove(itemId);
                    }

                    if (await _store.GetSourceAsync(itemId, cancellationToken).ConfigureAwait(false) is not { } stored)
                    {
                        continue;
                    }

                    var source = ToIndexed(stored);
                    items[itemId] = source;
                    if (source.Sha256 is { } sha)
                    {
                        shas[sha] = (shas.TryGetValue(sha, out var others) ? others : []).Add(itemId);
                    }
                }

                Volatile.Write(ref _snapshot, current with { Items = items.ToImmutable(), ShaToItems = shas.ToImmutable() });
                _staleItems.Clear();
            }
            catch
            {
                _staleItems.UnionWith(list);
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }

        Raise(list);
    }

    /// <summary>
    /// Reads what is published of contents again. A content that no longer exists, or has nothing published, leaves the index.
    /// </summary>
    /// <param name="sha256s">The hashes of the contents.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RefreshMediaAsync(IEnumerable<string> sha256s, CancellationToken cancellationToken)
    {
        var requested = sha256s.ToList();
        IReadOnlyCollection<Guid> affected = [];
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = requested.Concat(_staleMedia).Distinct(StringComparer.Ordinal).ToList();
            if (list.Count == 0)
            {
                return;
            }

            try
            {
                var current = Volatile.Read(ref _snapshot);
                var media = current.Media.ToBuilder();
                foreach (var sha in list)
                {
                    var stored = await _store.GetMediaAsync(sha, cancellationToken).ConfigureAwait(false);
                    if (GetView(stored, stored is null ? [] : await _store.GetRenditionsAsync(sha, cancellationToken).ConfigureAwait(false)) is { } view)
                    {
                        media[sha] = view;
                    }
                    else
                    {
                        media.Remove(sha);
                    }
                }

                Volatile.Write(ref _snapshot, current with { Media = media.ToImmutable() });
                _staleMedia.Clear();
                affected = [.. list.SelectMany(sha => current.ShaToItems.TryGetValue(sha, out var members) ? members : []).Distinct()];
            }
            catch
            {
                _staleMedia.UnionWith(list);
                throw;
            }
        }
        finally
        {
            _writeLock.Release();
        }

        Raise(affected);
    }

    /// <summary>
    /// Reads again what failed refreshes left out.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    public async Task RefreshStaleAsync(CancellationToken cancellationToken)
    {
        await RefreshItemsAsync([], cancellationToken).ConfigureAwait(false);
        await RefreshMediaAsync([], cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Gets a library file.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The file, or <c>null</c> when it is not recorded.</returns>
    public HlsIndexedSource? GetSource(Guid itemId)
        => Volatile.Read(ref _snapshot).Items.TryGetValue(itemId, out var source) ? source : null;

    /// <summary>
    /// Gets what is published of the content of a library file.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The published renditions, or <c>null</c> when nothing is.</returns>
    public HlsMediaView? GetMediaForItem(Guid itemId)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Items.TryGetValue(itemId, out var source) && source.Sha256 is { } sha && snapshot.Media.TryGetValue(sha, out var view) ? view : null;
    }

    /// <summary>
    /// Gets the items whose content has something published.
    /// </summary>
    /// <returns>The items.</returns>
    public IReadOnlyCollection<Guid> GetPublishedItems()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return [.. snapshot.Media.Keys.SelectMany(sha => snapshot.ShaToItems.TryGetValue(sha, out var items) ? items : [])];
    }

    /// <summary>
    /// Gets the library files of a content.
    /// </summary>
    /// <param name="sha256">The hash of the content.</param>
    /// <returns>The items.</returns>
    public IReadOnlyCollection<Guid> GetItemsForSha(string sha256)
        => Volatile.Read(ref _snapshot).ShaToItems.TryGetValue(sha256, out var items) ? items : ImmutableHashSet<Guid>.Empty;

    private void Raise(IReadOnlyCollection<Guid> affected)
    {
        if (affected.Count > 0)
        {
            Changed?.Invoke(this, affected);
        }
    }

    private static HlsIndexedSource ToIndexed(HlsSourceRecord source)
        => new(source.File.ItemId, source.File.LibraryId, source.File.Path, source.Sha256);

    // Video by quality from the highest down, audio and subtitles by stream index.
    private static HlsMediaView? GetView(HlsMediaRecord? media, IEnumerable<HlsRenditionRecord> renditions)
    {
        if (media is null)
        {
            return null;
        }

        var published = renditions
            .Where(r => r.State == HlsRenditionState.Published && r.Base is not null && r.RelativeDirectory is not null && r.Summary is not null)
            .Select(r => new HlsPublishedRendition(r.Key, r.Base!, r.RelativeDirectory!, r.Summary!))
            .ToList();
        if (published.Count == 0)
        {
            return null;
        }

        return new HlsMediaView(
            media.Sha256,
            media.MediaKey,
            media.Probe.DurationSeconds,
            media.Probe.VideoIndex,
            media.Probe.Audio.Count,
            [.. published.Where(r => r.Key.StartsWith('v')).OrderByDescending(r => r.Summary.Quality)],
            [.. published.Where(r => r.Key.StartsWith('a')).OrderBy(r => r.Summary.SourceIndex)],
            [.. published.Where(r => r.Key.StartsWith('s')).OrderBy(r => r.Summary.SourceIndex)]);
    }

    private sealed record Snapshot(
        ImmutableDictionary<Guid, HlsIndexedSource> Items,
        ImmutableDictionary<string, HlsMediaView> Media,
        ImmutableDictionary<string, ImmutableHashSet<Guid>> ShaToItems);
}
