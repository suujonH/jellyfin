using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Playlist;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Hls;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// Plays pre-encoded HLS from the published index; only the media playlists of video and audio read the database, for the
/// durations of their segments.
/// </summary>
public sealed class HlsPlaybackService : IHlsPlaybackService
{
    private const int SigningKeyBytes = 32;

    private readonly HlsSettings _settings;
    private readonly HlsPublishedIndex _index;
    private readonly HlsJobStore _store;
    private readonly IHlsStoreFactory _storeFactory;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILogger<HlsPlaybackService> _logger;
    private readonly Lock _keyLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsPlaybackService"/> class.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="index">The published index.</param>
    /// <param name="store">The data of pre-encoded HLS.</param>
    /// <param name="storeFactory">The factory of HLS stores.</param>
    /// <param name="configurationManager">The configuration manager, which saves a new signing key.</param>
    /// <param name="logger">The logger.</param>
    public HlsPlaybackService(
        HlsSettings settings,
        HlsPublishedIndex index,
        HlsJobStore store,
        IHlsStoreFactory storeFactory,
        IConfigurationManager configurationManager,
        ILogger<HlsPlaybackService> logger)
    {
        _settings = settings;
        _index = index;
        _store = store;
        _storeFactory = storeFactory;
        _configurationManager = configurationManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string? IssueGrant(Guid itemId)
    {
        if (GetMedia(itemId) is null)
        {
            return null;
        }

        var hours = _settings.Current.Options.PlaybackTokenHours;
        return HlsPlaybackGrant.Create(itemId, DateTimeOffset.UtcNow.AddHours(hours), GetOrCreateSigningKey());
    }

    /// <inheritdoc />
    public Guid? VerifyGrant(string grant)
    {
        // No key means no grant was issued under the current settings.
        var key = _settings.Current.Options.PlaybackSigningKey;
        return !string.IsNullOrEmpty(key) && HlsPlaybackGrant.TryVerify(grant, Convert.FromBase64String(key), DateTimeOffset.UtcNow, out var itemId)
            ? itemId
            : null;
    }

    /// <inheritdoc />
    public string? GetMasterPlaylist(Guid itemId, string name)
    {
        if (GetMedia(itemId) is not { } media || !HlsMasterPlaylistWriter.TryParsePlaylistName(name, out var quality, out var audioIndex))
        {
            return null;
        }

        // Only what is published now: a quality or an audio track that was withdrawn is not offered.
        if ((quality != 0 && media.Find(HlsRenditionKind.Video, quality) is null)
            || (audioIndex is { } audio && media.Find(HlsRenditionKind.Audio, audio) is null))
        {
            return null;
        }

        return HlsMasterPlaylistWriter.Write(media, quality, audioIndex);
    }

    /// <inheritdoc />
    public async Task<string?> GetMediaPlaylistAsync(Guid itemId, HlsRenditionKind kind, int index, CancellationToken cancellationToken)
    {
        if (GetMedia(itemId) is not { } media || media.Find(kind, index) is not { } rendition || OpenStore(rendition.Base) is not { } store)
        {
            return null;
        }

        // Each file at its direct address when clients read the store, otherwise relative to the playlist, for the server to send.
        // A relative address names the attempt, so a playback keeps the files of its playlist after the rendition is published again.
        var attempt = rendition.RelativeDirectory[(rendition.RelativeDirectory.LastIndexOf('/') + 1)..];
        string GetUri(string file)
            => store.GetClientAddress(rendition.RelativeDirectory + "/" + file)?.AbsoluteUri ?? attempt + "/" + file;

        if (kind == HlsRenditionKind.Subtitle)
        {
            // One file for the whole content.
            var ticks = Math.Max(1, TimeSpan.FromSeconds(media.DurationSeconds).Ticks);
            return HlsPlaylistWriter.WriteMedia([ticks], null, _ => GetUri(HlsRenditionPaths.SubtitleFile));
        }

        // The rendition may have been published again since the index was read: its segments are those of the directory it names.
        var segments = await _store.GetPublishedSegmentsAsync(media.Sha256, rendition.Key, cancellationToken).ConfigureAwait(false);
        if (segments is null || segments.Durations.Count == 0 || !string.Equals(segments.RelativeDirectory, rendition.RelativeDirectory, StringComparison.Ordinal))
        {
            return null;
        }

        return HlsPlaylistWriter.WriteMedia(
            segments.Durations.Select(d => TimeSpan.FromSeconds(d).Ticks).ToList(),
            GetUri(HlsRenditionPaths.InitFile),
            i => GetUri(HlsRenditionPaths.GetSegmentFile(i)));
    }

    /// <inheritdoc />
    public HlsDeliveredFile? GetFile(Guid itemId, HlsRenditionKind kind, int index, string attempt, string name)
    {
        if (GetMedia(itemId) is not { } media
            || media.Find(kind, index) is not { } rendition
            || !HlsRenditionPaths.IsAllowedFile(kind, name, HlsEncodingProfile.GetExpectedSegments(media.DurationSeconds)))
        {
            return null;
        }

        // The published attempt, or one it replaced that is kept for the playbacks that still have its playlist; in the same root,
        // so one that a publication in another root replaced is not found.
        var directory = rendition.RelativeDirectory[..(rendition.RelativeDirectory.LastIndexOf('/') + 1)] + attempt;
        if (!HlsRenditionPaths.IsValidRelativeDirectory(directory)
            || OpenStore(rendition.Base) is not { } store
            || store.GetServerPath(directory + "/" + name) is not { } location)
        {
            return null;
        }

        var contentType = kind switch
        {
            HlsRenditionKind.Subtitle => "text/vtt",
            _ when !string.Equals(name, HlsRenditionPaths.InitFile, StringComparison.Ordinal) => "video/iso.segment",
            HlsRenditionKind.Audio => "audio/mp4",
            _ => "video/mp4"
        };
        return new HlsDeliveredFile(location, store.IsLocal, contentType);
    }

    // What can be played of an item: pre-encoded HLS is on, its library is chosen and its content is playable.
    private HlsMediaView? GetMedia(Guid itemId)
        => _index.GetSource(itemId) is { } source
            && _settings.Current.IsLibraryEligible(source.LibraryId)
            && _index.GetMediaForItem(itemId) is { Playable: true } media
                ? media
                : null;

    private IHlsStore? OpenStore(string root)
    {
        try
        {
            return _storeFactory.Create(root);
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("The HLS store {Root} of a published rendition cannot be opened: {Error}", root, ex.Message);
            return null;
        }
    }

    // The key is created before the first grant is issued and saved with the settings; the settings take it when they are saved.
    private byte[] GetOrCreateSigningKey()
    {
        lock (_keyLock)
        {
            var options = _configurationManager.GetHlsOptions();
            if (string.IsNullOrEmpty(options.PlaybackSigningKey))
            {
                // A copy, so the settings in use never change while the new ones are checked and saved.
                options = JsonSerializer.Deserialize<HlsOptions>(JsonSerializer.SerializeToUtf8Bytes(options))!;
                options.PlaybackSigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(SigningKeyBytes));
                _configurationManager.SaveConfiguration(HlsConfigurationStore.StoreKey, options);
                _logger.LogInformation("Created the signing key of the playback grants of pre-encoded HLS");
            }

            return Convert.FromBase64String(options.PlaybackSigningKey);
        }
    }
}
