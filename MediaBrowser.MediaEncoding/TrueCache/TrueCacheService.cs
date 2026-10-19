using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.MediaEncoding.TrueCache;

/// <summary>
/// Looks up <c>__true_cache__</c> entries and keeps the results until the entry or the resource changes.
/// </summary>
public sealed class TrueCacheService : ITrueCacheService
{
    // Scans ask about every file of a folder; the folder's cache folder is looked at once per interval.
    private static readonly TimeSpan _folderCheckInterval = TimeSpan.FromSeconds(10);

    private readonly ILogger<TrueCacheService> _logger;
    private readonly IServerConfigurationManager _config;
    private readonly TrueCacheLoader _loader;
    private readonly ConcurrentDictionary<string, (bool Exists, DateTime CheckedAt)> _folders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (Stamp Stamp, TrueCacheLookup Result, DateTime CheckedAt)> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TrueCacheRejection> _rejections = new(StringComparer.Ordinal);
    // When a valid entry appeared or went away while the service watched. Neither leaves a usable time on disk:
    // a removed entry is gone, and a copied entry can keep an older modification time than the item's last refresh.
    private readonly ConcurrentDictionary<string, DateTime> _transitions = new(StringComparer.Ordinal);

    // Resources seen without an entry, and folders seen without a cache folder before one appeared:
    // an entry found later for them is new, while one found at the first look may have been there all along.
    private readonly ConcurrentDictionary<string, bool> _undeclared = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _appearedCacheFolders = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="TrueCacheService"/> class.
    /// </summary>
    /// <param name="logger">The logger.</param>
    /// <param name="localization">The localization manager.</param>
    /// <param name="config">The server configuration.</param>
    public TrueCacheService(ILogger<TrueCacheService> logger, ILocalizationManager localization, IServerConfigurationManager config)
    {
        _logger = logger;
        _config = config;
        _loader = new TrueCacheLoader(logger, localization);
    }

    /// <inheritdoc />
    public TrueCacheLookup Lookup(string resourcePath) => Lookup(resourcePath, false);

    /// <inheritdoc />
    public TrueCacheLookup Validate(string resourcePath) => Lookup(resourcePath, true);

    /// <inheritdoc />
    public TrueCacheLookup LookupAlbum(string albumPath)
    {
        if (string.IsNullOrEmpty(albumPath) || !HasCacheFolder(Path.TrimEndingDirectorySeparator(albumPath), false))
        {
            return TrueCacheLookup.None;
        }

        return _loader.LoadAlbum(albumPath);
    }

    /// <inheritdoc />
    public IReadOnlyList<TrueCacheRejection> GetRejections()
        => _rejections.Values.OrderByDescending(r => r.CheckedAt).ToArray();

    /// <inheritdoc />
    public bool HasDeclarationChanged(string resourcePath, DateTime since)
    {
        var lookup = Lookup(resourcePath);
        if (lookup.State == TrueCacheState.Rejected)
        {
            return false;
        }

        // An item still holds what a removed entry provided, such as chapter images and trickplay tiles,
        // or has never been given what a newly added entry provides.
        if (_transitions.TryGetValue(resourcePath, out var changed) && changed > since)
        {
            return true;
        }

        if (!lookup.IsValid)
        {
            return false;
        }

        var meta = new FileInfo(Path.Combine(lookup.Entry.Directory, TrueCacheLayout.MetaFile));
        return meta.Exists && meta.LastWriteTimeUtc > since;
    }

    private TrueCacheLookup Lookup(string resourcePath, bool force)
    {
        if (string.IsNullOrEmpty(resourcePath) || !Path.IsPathFullyQualified(resourcePath))
        {
            return TrueCacheLookup.None;
        }

        var directory = TrueCacheLayout.GetEntryDirectory(resourcePath);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(resourcePath));
        if (directory is null || parent is null || !HasCacheFolder(parent, force))
        {
            Forget(resourcePath);
            return TrueCacheLookup.None;
        }

        if (!Directory.Exists(directory))
        {
            Forget(resourcePath);
            _undeclared[resourcePath] = true;
            return TrueCacheLookup.None;
        }

        var requirements = GetRequirements();
        var stamp = Stamp.Of(resourcePath, directory, requirements);
        var now = DateTime.UtcNow;

        // A rejected entry is checked again after a while: files added to its subfolders do not change the stamp.
        var known = _entries.TryGetValue(resourcePath, out var kept);
        if (!force && known && kept.Stamp == stamp
            && (kept.Result.State != TrueCacheState.Rejected || now - kept.CheckedAt < _folderCheckInterval))
        {
            return kept.Result;
        }

        var result = _loader.Load(resourcePath, requirements);
        _entries[resourcePath] = (stamp, result, now);
        if (result.IsValid && (_undeclared.TryRemove(resourcePath, out _) || (!known && _appearedCacheFolders.ContainsKey(parent))))
        {
            _transitions[resourcePath] = now;
        }

        if (result.State == TrueCacheState.Rejected)
        {
            var isNew = !_rejections.TryGetValue(resourcePath, out var previous) || !string.Equals(previous.Reason, result.Reason, StringComparison.Ordinal);
            _rejections[resourcePath] = new TrueCacheRejection(resourcePath, result.Reason!, DateTime.UtcNow);
            if (isNew)
            {
                _logger.LogWarning("Rejecting {Path}: its __true_cache__ entry is incomplete or invalid ({Reason})", resourcePath, result.Reason);
            }
        }
        else
        {
            _rejections.TryRemove(resourcePath, out _);
        }

        return result;
    }

    private void Forget(string resourcePath)
    {
        if (_entries.TryRemove(resourcePath, out var kept) && kept.Result.IsValid)
        {
            _transitions[resourcePath] = DateTime.UtcNow;
        }

        _rejections.TryRemove(resourcePath, out _);
    }

    private bool HasCacheFolder(string folder, bool force)
    {
        var now = DateTime.UtcNow;
        var known = _folders.TryGetValue(folder, out var kept);
        if (!force && known && now - kept.CheckedAt < _folderCheckInterval)
        {
            return kept.Exists;
        }

        var exists = Directory.Exists(Path.Combine(folder, TrueCacheLayout.FolderName));
        _folders[folder] = (exists, now);
        if (exists && known && !kept.Exists)
        {
            _appearedCacheFolders[folder] = true;
        }

        return exists;
    }

    private TrueCacheRequirements GetRequirements()
    {
        var configuration = _config.Configuration;
        var trickplay = configuration.TrickplayOptions;
        return new TrueCacheRequirements(trickplay.WidthResolutions, trickplay.Interval, configuration.DummyChapterDuration);
    }

    private readonly record struct Stamp(DateTime Entry, DateTime Meta, long MetaLength, DateTime Resource, long ResourceLength, string Requirements)
    {
        public static Stamp Of(string resourcePath, string directory, TrueCacheRequirements requirements)
        {
            var meta = new FileInfo(Path.Combine(directory, TrueCacheLayout.MetaFile));
            var resource = new FileInfo(resourcePath);
            return new Stamp(
                Directory.GetLastWriteTimeUtc(directory),
                meta.Exists ? meta.LastWriteTimeUtc : DateTime.MinValue,
                meta.Exists ? meta.Length : -1,
                resource.Exists ? resource.LastWriteTimeUtc : Directory.GetLastWriteTimeUtc(resourcePath),
                resource.Exists ? resource.Length : -1,
                string.Join(',', requirements.TrickplayWidths) + "|" + requirements.TrickplayInterval + "|" + requirements.DummyChapterDuration);
        }
    }
}
