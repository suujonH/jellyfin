using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.MediaEncoding.Hls.Playlist;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Hls;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// Plays pre-encoded HLS from the published index; only the media playlists of video and audio read the database, for the
/// durations of their segments. The rules of the original file read the current request.
/// </summary>
public sealed class HlsPlaybackService : IHlsPlaybackService
{
    private const int SigningKeyBytes = 32;

    // The route of the playback of a source, which its grant follows.
    private const string PlaybackRoute = "/HlsPlayback";

    // The claim the authentication of the server gives a request with an API key (InternalClaimTypes.IsApiKey of Jellyfin.Api).
    private const string IsApiKeyClaim = "Jellyfin-IsApiKey";

    private readonly HlsSettings _settings;
    private readonly HlsPublishedIndex _index;
    private readonly HlsJobStore _store;
    private readonly IHlsStoreFactory _storeFactory;
    private readonly IConfigurationManager _configurationManager;
    private readonly ILibraryManager _libraryManager;
    private readonly ILocalizationManager _localization;
    private readonly INetworkManager _networkManager;
    private readonly INetworkStorageManager _networkStorage;
    private readonly IHttpContextAccessor _httpContextAccessor;
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
    /// <param name="libraryManager">The library manager, which gives the ids of the sources.</param>
    /// <param name="localization">The localization manager, which names the sources.</param>
    /// <param name="networkManager">The network manager, which tells the local network.</param>
    /// <param name="networkStorage">The network storage manager, which tells the archived originals.</param>
    /// <param name="httpContextAccessor">The accessor of the current request.</param>
    /// <param name="logger">The logger.</param>
    public HlsPlaybackService(
        HlsSettings settings,
        HlsPublishedIndex index,
        HlsJobStore store,
        IHlsStoreFactory storeFactory,
        IConfigurationManager configurationManager,
        ILibraryManager libraryManager,
        ILocalizationManager localization,
        INetworkManager networkManager,
        INetworkStorageManager networkStorage,
        IHttpContextAccessor httpContextAccessor,
        ILogger<HlsPlaybackService> logger)
    {
        _settings = settings;
        _index = index;
        _store = store;
        _storeFactory = storeFactory;
        _configurationManager = configurationManager;
        _libraryManager = libraryManager;
        _localization = localization;
        _networkManager = networkManager;
        _networkStorage = networkStorage;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    /// <inheritdoc />
    public bool IsPublished(Guid itemId) => GetMedia(itemId) is not null;

    /// <inheritdoc />
    public bool IsOriginalAllowed(Guid itemId, User? user) => GetMedia(itemId) is null || IsOriginalPermitted(user);

    /// <inheritdoc />
    public bool PrefersOriginal(BaseItem item, User? user)
    {
        ArgumentNullException.ThrowIfNull(item);
        return GetMedia(item.Id) is not null && IsOriginalPermitted(user) && IsLocalRequest() && item.IsFileProtocol && !item.IsShortcut;
    }

    /// <inheritdoc />
    public IReadOnlyList<MediaSourceInfo> ArrangeMediaSources(BaseItem item, IReadOnlyList<MediaSourceInfo> sources, User user)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(user);

        // Each original follows the rules of its own item, as it does when it is played: an alternate version too.
        var permitted = IsOriginalPermitted(user);
        bool IsAllowed(MediaSourceInfo source)
            => permitted || GetMedia(Guid.TryParse(source.Id, out var id) ? id : item.Id) is null;

        var media = item is Video ? GetMedia(item.Id) : null;
        if (media is null)
        {
            return permitted ? sources : sources.Where(IsAllowed).ToArray();
        }

        if (!user.HasPermission(PermissionKind.EnableMediaPlayback))
        {
            return [];
        }

        var originals = sources.Where(s => IsAllowed(s) && !_networkStorage.IsArchived(s.ShortcutTarget)).ToList();
        var originalName = _localization.GetLocalizedString("HlsSourceOriginal");
        foreach (var source in originals)
        {
            var name = GetDescription(source) is { } description ? originalName + " (" + description + ")" : originalName;
            source.Name = originals.Count > 1 && !string.IsNullOrEmpty(source.Name) ? name + " · " + source.Name : name;
        }

        // The tracks keep what the published summaries do not carry, such as a forced subtitle, from the original of the item.
        var streams = sources.FirstOrDefault(s => string.Equals(s.Id, item.Id.ToString("N", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase))
            ?.MediaStreams ?? [];
        // An alternate version that is being resumed stays first, as the user data of the item resumes it.
        var resumed = sources.Count > 0 && originals.Contains(sources[0]) && !string.Equals(sources[0].Id, item.Id.ToString("N", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            ? sources[0]
            : null;
        var auto = BuildSource(item.Id, media, 0, streams);
        var qualities = media.Video.Select(v => BuildSource(item.Id, media, v.Summary.Quality, streams));
        return resumed is null
            ? [auto, .. originals, .. qualities]
            : [resumed, auto, .. originals.Where(s => s != resumed), .. qualities];
    }

    /// <inheritdoc />
    public int? GetQuality(Guid itemId, string? mediaSourceId)
    {
        if (string.IsNullOrEmpty(mediaSourceId) || GetMedia(itemId) is not { } media)
        {
            return null;
        }

        foreach (var quality in media.Video.Select(v => v.Summary.Quality).Prepend(0))
        {
            if (string.Equals(mediaSourceId, GetSourceId(itemId, quality), StringComparison.OrdinalIgnoreCase))
            {
                return quality;
            }
        }

        return null;
    }

    /// <inheritdoc />
    public Guid? GetOriginalId(BaseItem item) => HlsVersionCatalog.Identify(item)?.ItemId;

    /// <inheritdoc />
    public string? GetPlaylistUrl(BaseItem item, string? mediaSourceId, int? audioStreamIndex, User? user)
    {
        ArgumentNullException.ThrowIfNull(item);
        var itemId = item.Id;
        var quality = GetQuality(itemId, mediaSourceId);
        // A version item stands for its own source of its original, unless the request names another one.
        if (quality is null && GetOriginalId(item) is { } originalId)
        {
            itemId = originalId;
            quality = GetQuality(originalId, string.IsNullOrEmpty(mediaSourceId) ? item.Id.ToString("N", CultureInfo.InvariantCulture) : mediaSourceId);
        }

        // A version item is only as visible as its original.
        if (quality is not { } found
            || GetMedia(itemId) is not { } media
            || (user is not null && (!user.HasPermission(PermissionKind.EnableMediaPlayback) || _libraryManager.GetItemById<BaseItem>(itemId, user) is null))
            || IssueGrant(itemId) is not { } grant)
        {
            return null;
        }

        return GetPlaylistUrl(grant, media, found, audioStreamIndex);
    }

    /// <inheritdoc />
    public bool PrepareForPlayback(Guid itemId, MediaSourceInfo source, string playSessionId, int? audioStreamIndex, int? subtitleStreamIndex, string serverUrl)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (GetMedia(itemId) is not { } media || GetQuality(itemId, source.Id) is not { } quality || IssueGrant(itemId) is not { } grant)
        {
            return false;
        }

        // The clients play the playlist as a transcoding, which never starts the encoder of the server.
        source.SupportsDirectPlay = false;
        source.SupportsDirectStream = false;
        source.SupportsTranscoding = true;
        source.TranscodingSubProtocol = MediaStreamProtocol.hls;
        source.TranscodingContainer = "mp4";
        // The requested tracks are the selected ones, as negotiation selects them for other sources; -1 turns subtitles off.
        if (audioStreamIndex is { } audio && media.Find(HlsRenditionKind.Audio, audio) is not null)
        {
            source.DefaultAudioStreamIndex = audio;
        }

        if (subtitleStreamIndex is { } subtitleIndex && (subtitleIndex == -1 || media.Find(HlsRenditionKind.Subtitle, subtitleIndex) is not null))
        {
            source.DefaultSubtitleStreamIndex = subtitleIndex;
        }

        source.TranscodingUrl = GetPlaylistUrl(grant, media, quality, audioStreamIndex ?? source.DefaultAudioStreamIndex)
            + "?PlaySessionId=" + Uri.EscapeDataString(playSessionId) + "&MediaSourceId=" + source.Id;
        source.Path = serverUrl.TrimEnd('/') + source.TranscodingUrl;

        foreach (var stream in source.MediaStreams.Where(s => s.Type == MediaStreamType.Subtitle))
        {
            if (media.Find(HlsRenditionKind.Subtitle, stream.Index) is { } subtitle)
            {
                stream.DeliveryMethod = SubtitleDeliveryMethod.External;
                stream.DeliveryUrl = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{PlaybackRoute}/{grant}/r/subtitles/{stream.Index}/{GetAttempt(subtitle)}/{HlsRenditionPaths.SubtitleFile}");
                stream.IsExternalUrl = false;
            }
        }

        return true;
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
        var attempt = GetAttempt(rendition);
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

    /// <summary>
    /// Gets what can be played of an item: pre-encoded HLS is on, the library of the item is chosen and its content is playable.
    /// </summary>
    /// <param name="index">The published index.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="itemId">The item.</param>
    /// <returns>The published renditions, or <c>null</c> when the item cannot be played.</returns>
    internal static HlsMediaView? GetPlayableMedia(HlsPublishedIndex index, HlsSettingsSnapshot settings, Guid itemId)
        => index.GetSource(itemId) is { } source
            && settings.IsLibraryEligible(source.LibraryId)
            && index.GetMediaForItem(itemId) is { Playable: true } media
                ? media
                : null;

    /// <summary>
    /// Builds the streams of a source of pre-encoded HLS. Their indexes are the ffprobe indexes of the original, so they match the
    /// tracks of the original, whose dispositions they keep.
    /// </summary>
    /// <param name="media">What is published.</param>
    /// <param name="quality">The quality, or 0 for every quality.</param>
    /// <param name="originalStreams">The streams of the original.</param>
    /// <returns>The streams.</returns>
    internal static List<MediaStream> BuildStreams(HlsMediaView media, int quality, IReadOnlyList<MediaStream> originalStreams)
    {
        MediaStream? Original(int index, MediaStreamType type) => originalStreams.FirstOrDefault(s => s.Index == index && s.Type == type);

        var video = GetVideo(media, quality);
        var streams = new List<MediaStream>
        {
            new()
            {
                Index = media.VideoIndex,
                Type = MediaStreamType.Video,
                Codec = "h264",
                Width = video.Width,
                Height = video.Height,
                RealFrameRate = (float)video.FramesPerSecond,
                AverageFrameRate = (float)video.FramesPerSecond,
                BitRate = video.Bandwidth,
                BitDepth = 8,
                PixelFormat = "yuv420p",
                IsDefault = true
            }
        };
        streams.AddRange(media.Audio.Select(a => new MediaStream
        {
            Index = a.Summary.SourceIndex,
            Type = MediaStreamType.Audio,
            Codec = "aac",
            Channels = a.Summary.Channels,
            SampleRate = 48000,
            Language = a.Summary.Language,
            Title = a.Summary.Title,
            BitRate = a.Summary.Bandwidth,
            IsDefault = a.Summary.Default,
            IsForced = Original(a.Summary.SourceIndex, MediaStreamType.Audio)?.IsForced ?? false,
            IsOriginal = Original(a.Summary.SourceIndex, MediaStreamType.Audio)?.IsOriginal ?? false,
            IsHearingImpaired = Original(a.Summary.SourceIndex, MediaStreamType.Audio)?.IsHearingImpaired ?? false
        }));
        streams.AddRange(media.Subtitles.Select(s => new MediaStream
        {
            Index = s.Summary.SourceIndex,
            Type = MediaStreamType.Subtitle,
            Codec = "webvtt",
            Language = s.Summary.Language,
            Title = s.Summary.Title,
            IsDefault = s.Summary.Default,
            IsForced = Original(s.Summary.SourceIndex, MediaStreamType.Subtitle)?.IsForced ?? false,
            IsHearingImpaired = Original(s.Summary.SourceIndex, MediaStreamType.Subtitle)?.IsHearingImpaired ?? false,
            // Sent as a file, but selected as the embedded track of the original is.
            SupportsExternalStream = true,
            DeliveryMethod = SubtitleDeliveryMethod.External
        }));
        return streams;
    }

    /// <summary>
    /// Gets the video a quality plays: "auto" has the size of the highest.
    /// </summary>
    /// <param name="media">What is published.</param>
    /// <param name="quality">The quality, or 0 for every quality.</param>
    /// <returns>The summary of the video.</returns>
    internal static HlsRenditionSummary GetVideo(HlsMediaView media, int quality)
        => (quality == 0 ? media.Video.MaxBy(v => v.Summary.Bandwidth) : media.Find(HlsRenditionKind.Video, quality))!.Summary;

    private static string GetPlaylistUrl(string grant, HlsMediaView media, int quality, int? audioStreamIndex)
        => string.Create(CultureInfo.InvariantCulture, $"{PlaybackRoute}/{grant}/{HlsMasterPlaylistWriter.GetPlaylistName(media, quality, audioStreamIndex)}.m3u8");

    // The directory of the published attempt of a rendition.
    private static string GetAttempt(HlsPublishedRendition rendition)
        => rendition.RelativeDirectory[(rendition.RelativeDirectory.LastIndexOf('/') + 1)..];

    // What an original source plays, such as "1080p 24fps", from its video stream.
    private static string? GetDescription(MediaSourceInfo source)
    {
        if (source.VideoStream is not { Width: > 0, Height: > 0 } video)
        {
            return null;
        }

        var (width, height) = Math.Abs((video.Rotation ?? 0) % 180) == 90
            ? (video.Height.Value, video.Width.Value)
            : (video.Width.Value, video.Height.Value);
        var description = string.Create(CultureInfo.InvariantCulture, $"{HlsEncodingLadder.GetName(width, height)}p");
        return (video.RealFrameRate ?? video.AverageFrameRate) is > 0 and { } fps
            ? string.Create(CultureInfo.InvariantCulture, $"{description} {Math.Round(fps)}fps")
            : description;
    }

    private HlsMediaView? GetMedia(Guid itemId) => GetPlayableMedia(_index, _settings.Current, itemId);

    // The original may be played next to pre-encoded HLS: with an API key, or as the settings and the user allow.
    private bool IsOriginalPermitted(User? user)
    {
        if (bool.TryParse(_httpContextAccessor.HttpContext?.User.FindFirst(IsApiKeyClaim)?.Value, out var isApiKey) && isApiKey)
        {
            return true;
        }

        var options = _settings.Current.Options;
        return options.AllowOriginal
            && ((options.AllowLocalOriginal && IsLocalRequest()) || user?.HasPermission(PermissionKind.EnableOriginalPlayback) == true);
    }

    private bool IsLocalRequest()
        => _httpContextAccessor.HttpContext is { } context && _networkManager.IsInLocalNetwork(context.GetNormalizedRemoteIP());

    // The id of a source is that of the version item of its quality.
    private string GetSourceId(Guid itemId, int quality)
        => HlsVersionCatalog.GetVersionId(_libraryManager, itemId, quality).ToString("N", CultureInfo.InvariantCulture);

    // The source of a quality, or of every quality for 0.
    private MediaSourceInfo BuildSource(Guid itemId, HlsMediaView media, int quality, IReadOnlyList<MediaStream> originalStreams)
    {
        var video = GetVideo(media, quality);
        var streams = BuildStreams(media, quality, originalStreams);

        var id = GetSourceId(itemId, quality);
        return new MediaSourceInfo
        {
            Id = id,
            Name = quality == 0 ? _localization.GetLocalizedString("HlsSourceAuto") : string.Create(CultureInfo.InvariantCulture, $"{quality}p"),
            Path = string.Create(CultureInfo.InvariantCulture, $"{PlaybackRoute}/Versions/{id}/master.m3u8"),
            Protocol = MediaProtocol.Http,
            Container = "hls",
            IsRemote = true,
            SupportsProbing = false,
            SupportsDirectPlay = false,
            SupportsDirectStream = false,
            SupportsTranscoding = true,
            TranscodingSubProtocol = MediaStreamProtocol.hls,
            TranscodingContainer = "mp4",
            RunTimeTicks = TimeSpan.FromSeconds(media.DurationSeconds).Ticks,
            Bitrate = video.Bandwidth + (media.Audio.Count > 0 ? media.Audio.Max(a => a.Summary.Bandwidth) : 0),
            MediaStreams = streams,
            DefaultAudioStreamIndex = media.Audio.FirstOrDefault(a => a.Summary.Default)?.Summary.SourceIndex ?? media.Audio.FirstOrDefault()?.Summary.SourceIndex,
            DefaultSubtitleStreamIndex = -1,
            ETag = media.Sha256[..16]
        };
    }

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
