using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Extractors;
using Jellyfin.MediaEncoding.Hls.Playlist;
using Jellyfin.MediaEncoding.Keyframes;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace Jellyfin.MediaEncoding.Hls.Plans;

/// <inheritdoc />
public class KeyframeSource : IKeyframeSource
{
    private readonly IKeyframeRepository _keyframeRepository;
    private readonly ITrueCacheService _trueCache;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerConfigurationManager _serverConfigurationManager;
    private readonly ILogger<KeyframeSource> _logger;
    private readonly IKeyframeExtractor[] _metadataExtractors;
    private readonly IKeyframeExtractor[] _probeExtractors;

    // ffprobe reads the whole file; at most two files are read at once.
    private readonly TaskScheduler _probeScheduler = new ConcurrentExclusiveSchedulerPair(TaskScheduler.Default, 2).ConcurrentScheduler;
    private readonly Dictionary<Guid, Task<KeyframeData?>> _probes = [];
    private readonly HashSet<Guid> _failedProbes = [];

    /// <summary>
    /// Initializes a new instance of the <see cref="KeyframeSource"/> class.
    /// </summary>
    /// <param name="keyframeRepository">An instance of the <see cref="IKeyframeRepository"/> interface.</param>
    /// <param name="trueCache">An instance of the <see cref="ITrueCacheService"/> interface.</param>
    /// <param name="libraryManager">An instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="serverConfigurationManager">An instance of the <see cref="IServerConfigurationManager"/> interface.</param>
    /// <param name="extractors">The keyframe extractors, each behind a <see cref="Cache.CacheDecorator"/> that stores what it reads.</param>
    /// <param name="logger">An instance of the <see cref="ILogger{KeyframeSource}"/> interface.</param>
    public KeyframeSource(
        IKeyframeRepository keyframeRepository,
        ITrueCacheService trueCache,
        ILibraryManager libraryManager,
        IServerConfigurationManager serverConfigurationManager,
        IEnumerable<IKeyframeExtractor> extractors,
        ILogger<KeyframeSource> logger)
    {
        _keyframeRepository = keyframeRepository;
        _trueCache = trueCache;
        _libraryManager = libraryManager;
        _serverConfigurationManager = serverConfigurationManager;
        _logger = logger;
        var all = extractors.ToArray();
        _metadataExtractors = all.Where(e => e.IsMetadataBased).ToArray();
        _probeExtractors = all.Where(e => !e.IsMetadataBased).ToArray();
    }

    /// <inheritdoc />
    public async Task<KeyframeData?> GetKeyframesAsync(Guid itemId, string mediaPath, MediaProtocol protocol, CancellationToken cancellationToken)
    {
        // A declared video is never read: its keyframes come from its __true_cache__ entry or not at all.
        // Playback passes the file it reads, which can be the target of a symlink; the entry sits next to the library item.
        var itemPath = _libraryManager.GetItemById(itemId)?.Path;
        var lookup = _trueCache.Lookup(string.IsNullOrEmpty(itemPath) ? mediaPath : itemPath);
        if (lookup.IsDeclared)
        {
            return lookup.Entry?.Keyframes;
        }

        var stored = _keyframeRepository.GetKeyframeData(itemId);
        if (stored.Count > 0)
        {
            return stored[0];
        }

        // A remote source is never read for its keyframes: the video is transcoded.
        if (protocol != MediaProtocol.File || !File.Exists(mediaPath))
        {
            return null;
        }

        var options = _serverConfigurationManager.GetEncodingOptions();
        if (DynamicHlsPlaylistGenerator.IsExtractionAllowedForFile(mediaPath, options.AllowOnDemandMetadataBasedKeyframeExtractionForExtensions))
        {
            foreach (var extractor in _metadataExtractors)
            {
                if (extractor.TryExtractKeyframes(itemId, mediaPath, out var keyframes))
                {
                    return keyframes;
                }
            }
        }

        if (options.KeyframeExtractionTimeoutSeconds <= 0 || _probeExtractors.Length == 0)
        {
            return null;
        }

        var probe = GetProbe(itemId, mediaPath);
        if (probe is null)
        {
            return null;
        }

        try
        {
            return await probe.WaitAsync(TimeSpan.FromSeconds(options.KeyframeExtractionTimeoutSeconds), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogInformation(
                "Reading the keyframes of {Path} takes longer than {Timeout} s; the video is transcoded and the keyframes are kept for the next playback",
                mediaPath,
                options.KeyframeExtractionTimeoutSeconds);
            return null;
        }
    }

    // Starts reading the keyframes with ffprobe, or joins the read already running; null when an earlier read found none.
    private Task<KeyframeData?>? GetProbe(Guid itemId, string mediaPath)
    {
        lock (_probes)
        {
            if (_failedProbes.Contains(itemId))
            {
                return null;
            }

            if (!_probes.TryGetValue(itemId, out var probe))
            {
                probe = Task.Factory.StartNew(() => Probe(itemId, mediaPath), CancellationToken.None, TaskCreationOptions.None, _probeScheduler);
                _probes.Add(itemId, probe);
                probe.ContinueWith(
                    completed =>
                    {
                        lock (_probes)
                        {
                            _probes.Remove(itemId);

                            // A file ffprobe finds no keyframes in is not read again until the server restarts.
                            if (completed.Result is null)
                            {
                                _failedProbes.Add(itemId);
                            }
                        }
                    },
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            return probe;
        }
    }

    private KeyframeData? Probe(Guid itemId, string mediaPath)
    {
        try
        {
            // The cache decorator stores what the extractor reads.
            foreach (var extractor in _probeExtractors)
            {
                if (extractor.TryExtractKeyframes(itemId, mediaPath, out var keyframes))
                {
                    return keyframes;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Reading the keyframes of {Path} failed", mediaPath);
        }

        return null;
    }
}
