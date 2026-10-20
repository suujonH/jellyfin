#pragma warning disable CA1826 // Do not use Enumerable methods on indexable collections

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using Jellyfin.MediaEncoding.Hls.Extractors;
using Jellyfin.MediaEncoding.Keyframes;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.TrueCache;
using Microsoft.Extensions.Logging;

namespace Jellyfin.MediaEncoding.Hls.Cache;

/// <inheritdoc />
public class CacheDecorator : IKeyframeExtractor
{
    private readonly IKeyframeRepository _keyframeRepository;
    private readonly IKeyframeExtractor _keyframeExtractor;
    private readonly ILogger<CacheDecorator> _logger;
    private readonly ITrueCacheService _trueCache;
    private readonly ILibraryManager _libraryManager;
    private readonly string _keyframeExtractorName;

    /// <summary>
    /// Initializes a new instance of the <see cref="CacheDecorator"/> class.
    /// </summary>
    /// <param name="keyframeRepository">An instance of the <see cref="IKeyframeRepository"/> interface.</param>
    /// <param name="keyframeExtractor">An instance of the <see cref="IKeyframeExtractor"/> interface.</param>
    /// <param name="logger">An instance of the <see cref="ILogger{CacheDecorator}"/> interface.</param>
    /// <param name="trueCache">An instance of the <see cref="ITrueCacheService"/> interface.</param>
    /// <param name="libraryManager">An instance of the <see cref="ILibraryManager"/> interface.</param>
    public CacheDecorator(IKeyframeRepository keyframeRepository, IKeyframeExtractor keyframeExtractor, ILogger<CacheDecorator> logger, ITrueCacheService trueCache, ILibraryManager libraryManager)
    {
        ArgumentNullException.ThrowIfNull(keyframeRepository);
        ArgumentNullException.ThrowIfNull(keyframeExtractor);

        _keyframeRepository = keyframeRepository;
        _keyframeExtractor = keyframeExtractor;
        _logger = logger;
        _trueCache = trueCache;
        _libraryManager = libraryManager;
        _keyframeExtractorName = keyframeExtractor.GetType().Name;
    }

    /// <inheritdoc />
    public bool IsMetadataBased => _keyframeExtractor.IsMetadataBased;

    /// <inheritdoc />
    public bool TryExtractKeyframes(Guid itemId, string filePath, [NotNullWhen(true)] out KeyframeData? keyframeData)
    {
        // A declared video is never read: its keyframes come from its __true_cache__ entry or not at all.
        // They are not persisted, so a replaced entry takes effect at once.
        // Playback passes the file it reads, which can be the target of a symlink; the entry sits next to the library item.
        var itemPath = _libraryManager.GetItemById(itemId)?.Path;
        var lookup = _trueCache.Lookup(string.IsNullOrEmpty(itemPath) ? filePath : itemPath);
        if (lookup.IsDeclared)
        {
            keyframeData = lookup.Entry?.Keyframes;
            return keyframeData is not null;
        }

        keyframeData = _keyframeRepository.GetKeyframeData(itemId).FirstOrDefault();
        if (keyframeData is null)
        {
            if (!_keyframeExtractor.TryExtractKeyframes(itemId, filePath, out var result))
            {
                _logger.LogDebug("Failed to extract keyframes using {ExtractorName}", _keyframeExtractorName);
                return false;
            }

            _logger.LogDebug("Successfully extracted keyframes using {ExtractorName}", _keyframeExtractorName);
            keyframeData = result;
            _keyframeRepository.SaveKeyframeDataAsync(itemId, keyframeData, CancellationToken.None).GetAwaiter().GetResult();
        }

        return true;
    }
}
