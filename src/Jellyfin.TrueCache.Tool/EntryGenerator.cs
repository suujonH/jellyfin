using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.ScheduledTasks.Tasks;
using Jellyfin.Extensions;
using Jellyfin.Extensions.Json;
using Jellyfin.MediaEncoding.Keyframes;
using MediaBrowser.Common.Net.S3;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.MediaEncoding.Probing;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Providers.Books;
using MediaBrowser.Providers.Books.OpenPackagingFormat;
using MediaBrowser.Providers.MediaInfo;
using Microsoft.Extensions.Logging;
using PDFtoImage;
using SharpCompress.Archives;
using Tomlyn;
using Tomlyn.Model;

namespace Jellyfin.TrueCache.Tool;

/// <summary>
/// Writes complete <c>__true_cache__</c> entries. Every value comes from the code the server would run on the resource.
/// </summary>
internal sealed class EntryGenerator
{
    private static readonly string[] _comicArchiveExtensions = [".cbz", ".cbr", ".cb7", ".cbt"];
    private static readonly HttpClient _httpClient = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private readonly ToolServices _services;
    private readonly ILogger _logger;

    public EntryGenerator(ToolServices services)
    {
        _services = services;
        _logger = services.LoggerFactory.CreateLogger<EntryGenerator>();
    }

    /// <summary>
    /// Writes the entry of a resource and validates it with the server's rules. The entry is put in place only when it is
    /// complete; otherwise the previous entry, or the lack of one, stays as it was.
    /// </summary>
    /// <param name="resourcePath">The media file or disc folder.</param>
    /// <param name="kind">The kind of resource.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The validation result of the written entry.</returns>
    public async Task<TrueCacheLookup> GenerateAsync(string resourcePath, TrueCacheKind kind, CancellationToken cancellationToken)
    {
        var directory = TrueCacheLayout.GetEntryDirectory(resourcePath) ?? throw new ArgumentException("The path has no parent folder.", nameof(resourcePath));
        var staged = await StageAsync(directory, staging => WriteEntryAsync(resourcePath, staging, kind, cancellationToken)).ConfigureAwait(false);
        return staged.IsValid ? _services.Loader.Load(resourcePath, _services.Requirements) : staged;
    }

    /// <summary>
    /// Writes the album-wide entry of an album folder: the loudness of all its tracks played in a row.
    /// </summary>
    /// <param name="albumPath">The album folder.</param>
    /// <param name="audioExtensions">The extensions of audio files.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The validation result of the written entry.</returns>
    public async Task<TrueCacheLookup> GenerateAlbumAsync(string albumPath, IReadOnlyCollection<string> audioExtensions, CancellationToken cancellationToken)
    {
        var tracks = Directory.EnumerateFiles(albumPath)
            .Where(f => audioExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .ToList();
        if (tracks.Count < 2)
        {
            throw new InvalidOperationException("An album needs at least two tracks; the server measures no album loudness otherwise.");
        }

        var concat = Path.Combine(_services.WorkDirectory, Guid.NewGuid().ToString("N") + ".concat");
        await File.WriteAllLinesAsync(concat, tracks.Select(t => string.Format(CultureInfo.InvariantCulture, "file '{0}'", t.Replace("'", @"'\''", StringComparison.Ordinal))), cancellationToken).ConfigureAwait(false);
        var lufs = await AudioNormalizationTask.CalculateLUFSAsync(
            _services.Encoder.EncoderPath,
            string.Format(CultureInfo.InvariantCulture, "-f concat -safe 0 -i \"{0}\"", concat),
            true,
            _logger,
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("ffmpeg reported no loudness for the album.");

        var root = new TomlTable
        {
            ["version"] = (long)TrueCacheManifest.CurrentVersion,
            ["kind"] = "album",
            ["album"] = new TomlTable { ["lufs"] = (double)lufs }
        };
        var staged = await StageAsync(TrueCacheLayout.GetAlbumEntryDirectory(albumPath), async staging =>
        {
            await File.WriteAllTextAsync(Path.Combine(staging, TrueCacheLayout.MetaFile), Toml.FromModel(root), cancellationToken).ConfigureAwait(false);
            return _services.Loader.LoadAlbum(albumPath, staging);
        }).ConfigureAwait(false);
        return staged.IsValid ? _services.Loader.LoadAlbum(albumPath) : staged;
    }

    /// <summary>
    /// Builds an entry in a staging folder and puts it in place only when it validates. The server rejects a resource whose
    /// entry folder is incomplete, so it must never see one: not while the entry is written, nor after a failed or cancelled attempt.
    /// The staging folder lies next to the entry, on the same volume, and its name starts with a dot, which the server ignores.
    /// </summary>
    /// <param name="directory">The entry folder.</param>
    /// <param name="build">Writes the entry into the folder it is given and validates it there.</param>
    /// <returns>The validation result of the staged entry.</returns>
    private static async Task<TrueCacheLookup> StageAsync(string directory, Func<string, Task<TrueCacheLookup>> build)
    {
        var staging = Path.Combine(Path.GetDirectoryName(directory)!, "." + Path.GetFileName(directory) + "." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var result = await build(staging).ConfigureAwait(false);
            if (result.IsValid)
            {
                Publish(staging, directory);
            }

            return result;
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, true);
            }
        }
    }

    /// <summary>
    /// Moves a complete entry into place. An existing entry is replaced only now; between the two moves the resource has no entry.
    /// </summary>
    private static void Publish(string staging, string directory)
    {
        if (!Directory.Exists(directory))
        {
            Directory.Move(staging, directory);
            return;
        }

        var previous = staging + ".previous";
        Directory.Move(directory, previous);
        try
        {
            Directory.Move(staging, directory);
        }
        catch
        {
            Directory.Move(previous, directory);
            throw;
        }

        Directory.Delete(previous, true);
    }

    private async Task<TrueCacheLookup> WriteEntryAsync(string resourcePath, string directory, TrueCacheKind kind, CancellationToken cancellationToken)
    {
        var root = new TomlTable
        {
            ["version"] = (long)TrueCacheManifest.CurrentVersion,
            ["kind"] = KindName(kind)
        };

        // A .strm with an entry is served like a local file, so its entry needs everything a local file's entry needs.
        // Everything is read from a local copy of the target, with the same code as for a local file.
        var isShortcut = (kind is TrueCacheKind.Video or TrueCacheKind.Audio) && TrueCacheLayout.IsShortcut(resourcePath);
        var mediaPath = isShortcut ? await DownloadShortcutTargetAsync(resourcePath, cancellationToken).ConfigureAwait(false) : resourcePath;
        try
        {
            if (File.Exists(resourcePath))
            {
                // The size of a .strm is the size of the media it names; the modification time is that of the .strm.
                var info = new FileInfo(resourcePath);
                root["source"] = new TomlTable
                {
                    ["size"] = new FileInfo(mediaPath).Length,
                    ["modified"] = new TomlDateTime(new DateTimeOffset(info.LastWriteTimeUtc), 3, TomlDateTimeKind.OffsetDateTimeByZ)
                };
            }

            switch (kind)
            {
                case TrueCacheKind.Video:
                case TrueCacheKind.Dvd:
                case TrueCacheKind.BluRay:
                    await WriteVideoAsync(resourcePath, mediaPath, directory, kind, root, cancellationToken).ConfigureAwait(false);
                    break;
                case TrueCacheKind.Audio:
                    await WriteAudioAsync(resourcePath, mediaPath, directory, root, cancellationToken).ConfigureAwait(false);
                    break;
                case TrueCacheKind.Book:
                    await WriteBookAsync(resourcePath, directory, root, cancellationToken).ConfigureAwait(false);
                    break;
                case TrueCacheKind.Photo:
                    root["photo"] = PhotoTags.Read(resourcePath, _services.ImageEncoder, _logger);
                    break;
                default:
                    throw new ArgumentException("Albums are written with the album command.", nameof(kind));
            }
        }
        finally
        {
            if (isShortcut)
            {
                File.Delete(mediaPath);
            }
        }

        await File.WriteAllTextAsync(Path.Combine(directory, TrueCacheLayout.MetaFile), Toml.FromModel(root), cancellationToken).ConfigureAwait(false);
        return _services.Loader.Load(resourcePath, directory, _services.Requirements);
    }

    private static string KindName(TrueCacheKind kind) => kind switch
    {
        TrueCacheKind.Video => "video",
        TrueCacheKind.Audio => "audio",
        TrueCacheKind.Dvd => "dvd",
        TrueCacheKind.BluRay => "bluray",
        TrueCacheKind.Book => "book",
        TrueCacheKind.Photo => "photo",
        _ => "album"
    };

    private static void MoveInto(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(source, target, true);
    }

    /// <summary>
    /// Downloads the target of a .strm into the work folder. The copy keeps the extension of the target, which decides,
    /// as for a local file, how the keyframes are read.
    /// </summary>
    private async Task<string> DownloadShortcutTargetAsync(string resourcePath, CancellationToken cancellationToken)
    {
        var target = ReadShortcutTarget(resourcePath);
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("Only http, https and s3 targets can be read in full; " + resourcePath + " names " + target + ".");
        }

        var copy = Path.Combine(_services.WorkDirectory, Guid.NewGuid().ToString("N") + Path.GetExtension(uri.AbsolutePath));
        _logger.LogInformation("Downloading the target of {Path}", resourcePath);
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        try
        {
            var file = File.Create(copy);
            await using (file.ConfigureAwait(false))
            {
                await response.Content.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            // A batch goes on with the next resource; a partial copy of a large media file must not stay behind.
            File.Delete(copy);
            throw;
        }

        return copy;
    }

    private string ReadShortcutTarget(string path)
    {
        var target = File.ReadAllLines(path)
            .Select(l => l.Replace("\t", string.Empty, StringComparison.Ordinal).Trim())
            .FirstOrDefault(l => l.Length != 0 && !l.StartsWith('#'))
            ?? throw new InvalidOperationException(path + " names no target.");

        // An s3:// target is read through an address presigned with the server's network storage accesses.
        return _services.NetworkStorage.ResolveShortcutTarget(target)
            ?? throw new InvalidOperationException("No network storage access covers " + target + "; pass --server-data with the server's data folder.");
    }

    // The entry describes resourcePath; mediaPath is the file read: the resource itself, or the local copy of the target of a .strm.
    private async Task WriteVideoAsync(string resourcePath, string mediaPath, string directory, TrueCacheKind kind, TomlTable root, CancellationToken cancellationToken)
    {
        var encoder = _services.Encoder;
        var probePath = mediaPath;
        BlurayDiscInfo? bluRay = null;
        IReadOnlyList<string> vobs = [];

        // Disc folders are probed through their first playable file, as the probe provider does.
        if (kind == TrueCacheKind.BluRay)
        {
            bluRay = _services.BluRayExaminer.GetDiscInfo(resourcePath);
            probePath = bluRay.Files is { Length: > 0 } files ? files[0] : throw new InvalidOperationException("The Blu-ray folder has no playable files.");
        }
        else if (kind == TrueCacheKind.Dvd)
        {
            vobs = encoder.GetPrimaryPlaylistVobFiles(resourcePath, null);
            probePath = vobs.Count > 0 ? vobs[0] : throw new InvalidOperationException("The DVD folder has no playable .vob files.");
        }

        var json = await ProbeAsync(probePath, MediaProtocol.File, DlnaProfileType.Video, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, TrueCacheLayout.ProbeFile), json, cancellationToken).ConfigureAwait(false);
        var info = _services.Loader.NormalizeProbe(json, resourcePath, kind);

        var video = new TomlTable();
        var runtime = info.RunTimeTicks ?? 0;
        if (kind == TrueCacheKind.Dvd)
        {
            // The probe provider sums the runtime of all playable .vob files.
            foreach (var vob in vobs.Skip(1))
            {
                var vobJson = await ProbeAsync(vob, MediaProtocol.File, DlnaProfileType.Video, cancellationToken).ConfigureAwait(false);
                runtime += _services.Loader.NormalizeProbe(vobJson, resourcePath, kind).RunTimeTicks ?? 0;
            }

            video["runtime_ticks"] = runtime;
        }

        if (bluRay is not null)
        {
            var stored = JsonSerializer.Deserialize<BlurayDiscInfo>(JsonSerializer.Serialize(bluRay, JsonDefaults.Options), JsonDefaults.Options)!;
            stored.Files = bluRay.Files.Select(f => Path.GetRelativePath(resourcePath, f)).ToArray();
            await File.WriteAllTextAsync(Path.Combine(directory, TrueCacheLayout.BdInfoFile), JsonSerializer.Serialize(stored, JsonDefaults.Options), cancellationToken).ConfigureAwait(false);
            if (bluRay.RunTimeTicks is > 0)
            {
                runtime = bluRay.RunTimeTicks.Value;
            }
        }

        if (kind == TrueCacheKind.Video && TrueCacheRules.TimestampContainers.Contains(info.Container, StringComparer.OrdinalIgnoreCase))
        {
            video["timestamp"] = ProbeResultNormalizer.GetMpegTimestamp(mediaPath).ToString().ToLowerInvariant();
        }

        var videoType = kind switch
        {
            TrueCacheKind.Dvd => VideoType.Dvd,
            TrueCacheKind.BluRay => VideoType.BluRay,
            _ => VideoType.VideoFile
        };
        var videoStream = info.MediaStreams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
        var imageSource = new MediaSourceInfo { VideoType = videoType, Protocol = MediaProtocol.File };

        if (kind != TrueCacheKind.Dvd)
        {
            if (videoStream is not null)
            {
                // The screen grabber takes the frame at 10% of the runtime.
                var offset = runtime > 0 ? TimeSpan.FromTicks(runtime / 10) : TimeSpan.FromSeconds(10);
                var grab = await encoder.ExtractVideoImage(mediaPath, info.Container, imageSource, videoStream, info.Video3DFormat, offset, cancellationToken).ConfigureAwait(false);
                MoveInto(grab, Path.Combine(directory, TrueCacheLayout.ScreenGrabFile));
            }

            await WriteEmbeddedImagesAsync(mediaPath, directory, info, imageSource, cancellationToken).ConfigureAwait(false);
        }

        if (videoStream is not null)
        {
            var chapters = TrueCacheRules.GetVideoChapters(info.Chapters, bluRay?.Chapters, runtime, _services.Config.Configuration.DummyChapterDuration);
            foreach (var chapter in TrueCacheRules.GetChaptersWithImages(chapters, runtime))
            {
                // The first chapter is grabbed a little later, as the chapter manager does, to avoid a black frame.
                var time = chapter.StartPositionTicks == 0 ? TimeSpan.FromTicks(Math.Min(TimeSpan.FromSeconds(15).Ticks, runtime)) : TimeSpan.FromTicks(chapter.StartPositionTicks);
                var image = await encoder.ExtractVideoImage(mediaPath, info.Container, imageSource, videoStream, info.Video3DFormat, time, cancellationToken).ConfigureAwait(false);
                MoveInto(image, Path.Combine(directory, TrueCacheLayout.ChaptersFolder, chapter.StartPositionTicks.ToString(CultureInfo.InvariantCulture) + ".jpg"));
            }
        }

        var source = new MediaSourceInfo
        {
            Id = Guid.NewGuid().ToString("N"),
            Path = mediaPath,
            Protocol = MediaProtocol.File,
            VideoType = videoType,
            Container = info.Container,
            RunTimeTicks = runtime,
            Video3DFormat = info.Video3DFormat,
            MediaStreams = info.MediaStreams,
            MediaAttachments = info.MediaAttachments
        };

        if (kind == TrueCacheKind.Video && videoStream is not null
            && runtime >= TimeSpan.FromMilliseconds(_services.Config.Configuration.TrickplayOptions.Interval).Ticks)
        {
            video["trickplay"] = await WriteTrickplayAsync(mediaPath, directory, info, source, videoStream, cancellationToken).ConfigureAwait(false);
        }

        await WriteSubtitlesAsync(directory, source, bluRay?.MediaStreams ?? info.MediaStreams, cancellationToken).ConfigureAwait(false);
        await WriteAttachmentsAsync(directory, source, cancellationToken).ConfigureAwait(false);

        if (kind == TrueCacheKind.Video && videoStream is not null)
        {
            var keyframes = ExtractKeyframes(mediaPath);
            await File.WriteAllTextAsync(Path.Combine(directory, TrueCacheLayout.KeyframesFile), JsonSerializer.Serialize(keyframes, JsonDefaults.Options), cancellationToken).ConfigureAwait(false);
        }

        if (video.Count > 0)
        {
            root["video"] = video;
        }
    }

    private Task<string> ProbeAsync(string path, MediaProtocol protocol, DlnaProfileType type, CancellationToken cancellationToken)
        => _services.Encoder.GetMediaInfoJson(
            new MediaInfoRequest
            {
                // Chapters are always stored; the server drops them for requests that do not ask for them.
                ExtractChapters = true,
                MediaType = type,
                MediaSource = new MediaSourceInfo { Path = path, Protocol = protocol }
            },
            cancellationToken);

    private async Task WriteEmbeddedImagesAsync(string resourcePath, string directory, MediaInfo info, MediaSourceInfo imageSource, CancellationToken cancellationToken)
    {
        var imageStreams = info.MediaStreams.Where(s => s.Type == MediaStreamType.EmbeddedImage).ToList();
        foreach (var type in TrueCacheRules.EmbeddedImageTypes)
        {
            var (attachment, stream, format) = TrueCacheRules.FindEmbeddedImage(type, info.MediaAttachments, imageStreams);
            string? image = null;
            if (attachment is not null)
            {
                image = await _services.Encoder.ExtractVideoImage(resourcePath, info.Container, imageSource, null, attachment.Index, format, cancellationToken).ConfigureAwait(false);
            }
            else if (stream is not null)
            {
                image = await _services.Encoder.ExtractVideoImage(resourcePath, info.Container, imageSource, stream, stream.Index, format, cancellationToken).ConfigureAwait(false);
            }

            if (image is not null)
            {
                MoveInto(image, Path.Combine(directory, TrueCacheLayout.ImagesFolder, type.ToString().ToLowerInvariant() + format.GetExtension()));
            }
        }
    }

    private async Task<TomlTableArray> WriteTrickplayAsync(string resourcePath, string directory, MediaInfo info, MediaSourceInfo source, MediaStream videoStream, CancellationToken cancellationToken)
    {
        var options = _services.Config.Configuration.TrickplayOptions;
        var resolutions = new TomlTableArray();
        foreach (var width in options.WidthResolutions.Select(w => TrueCacheRules.GetTrickplayWidth(w, videoStream.Width)).Distinct())
        {
            var images = await _services.Encoder.ExtractVideoImagesOnIntervalAccelerated(
                resourcePath,
                info.Container,
                source,
                videoStream,
                width,
                TimeSpan.FromMilliseconds(options.Interval),
                options.EnableHwAcceleration,
                options.EnableHwEncoding,
                options.ProcessThreads,
                options.Qscale,
                options.ProcessPriority,
                options.EnableKeyFrameOnlyExtraction,
                _services.EncodingHelper,
                cancellationToken).ConfigureAwait(false);
            try
            {
                var thumbnails = Directory.EnumerateFiles(images, "*.jpg").Order(StringComparer.Ordinal).ToList();
                var folder = string.Create(CultureInfo.InvariantCulture, $"{width} - {options.TileWidth}x{options.TileHeight}");
                var tiles = _services.Trickplay.CreateTiles(thumbnails, width, options, Path.Combine(directory, TrueCacheLayout.TrickplayFolder, folder));
                resolutions.Add(new TomlTable
                {
                    ["width"] = (long)tiles.Width,
                    ["height"] = (long)tiles.Height,
                    ["tile_width"] = (long)tiles.TileWidth,
                    ["tile_height"] = (long)tiles.TileHeight,
                    ["thumbnail_count"] = (long)tiles.ThumbnailCount,
                    ["interval"] = (long)tiles.Interval,
                    ["bandwidth"] = (long)tiles.Bandwidth
                });
            }
            finally
            {
                Directory.Delete(images, true);
            }
        }

        return resolutions;
    }

    private async Task WriteSubtitlesAsync(string directory, MediaSourceInfo source, IReadOnlyList<MediaStream> streams, CancellationToken cancellationToken)
    {
        var subtitles = streams.Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal && s.IsExtractableSubtitleStream).ToList();
        if (subtitles.Count == 0)
        {
            return;
        }

        foreach (var stream in subtitles)
        {
            stream.SupportsExternalStream = true;
        }

        source.MediaStreams = streams.ToList();
        await _services.SubtitleEncoder.ExtractAllExtractableSubtitles(source, cancellationToken).ConfigureAwait(false);
        foreach (var stream in subtitles)
        {
            var extension = "." + TrueCacheRules.GetExtractedSubtitleExtension(stream.Codec);
            var extracted = _services.PathManager.GetSubtitlePath(source.Id, stream.Index, extension)!;
            if (File.Exists(extracted))
            {
                MoveInto(extracted, Path.Combine(directory, TrueCacheLayout.SubtitlesFolder, stream.Index.ToString(CultureInfo.InvariantCulture) + extension));
            }
        }
    }

    private async Task WriteAttachmentsAsync(string directory, MediaSourceInfo source, CancellationToken cancellationToken)
    {
        var attachments = source.MediaAttachments.Where(a => !string.Equals(a.Codec, "mjpeg", StringComparison.OrdinalIgnoreCase)).ToList();
        if (attachments.Count == 0)
        {
            return;
        }

        // ffmpeg reads the playable input: the target a .strm names, not the .strm itself.
        await _services.AttachmentExtractor.ExtractAllAttachments(source.Path, source, cancellationToken).ConfigureAwait(false);
        var folder = _services.PathManager.GetAttachmentFolderPath(source.Id)!;
        foreach (var attachment in attachments)
        {
            var name = TrueCacheRules.GetAttachmentFileName(attachment);
            var extracted = Path.Combine(folder, name);
            if (File.Exists(extracted))
            {
                MoveInto(extracted, Path.Combine(directory, TrueCacheLayout.AttachmentsFolder, name));
            }
        }
    }

    private KeyframeData ExtractKeyframes(string path)
    {
        // The playlist generator reads Matroska cues; everything else, and Matroska without cues, is read with ffprobe.
        if (path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var cues = MediaEncoding.Keyframes.Matroska.MatroskaKeyframeExtractor.GetKeyframeData(path);
                if (cues.KeyframeTicks.Count > 0)
                {
                    return cues;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Reading the Matroska cues of {Path} failed; using ffprobe", path);
            }
        }

        return MediaEncoding.Keyframes.FfProbe.FfProbeKeyframeExtractor.GetKeyframeData(_services.Encoder.ProbePath, path);
    }

    // The entry describes resourcePath; mediaPath is the file read: the resource itself, or the local copy of the target of a .strm.
    private async Task WriteAudioAsync(string resourcePath, string mediaPath, string directory, TomlTable root, CancellationToken cancellationToken)
    {
        var json = await ProbeAsync(mediaPath, MediaProtocol.File, DlnaProfileType.Audio, cancellationToken).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(directory, TrueCacheLayout.ProbeFile), json, cancellationToken).ConfigureAwait(false);
        var info = _services.Loader.NormalizeProbe(json, resourcePath, TrueCacheKind.Audio);

        var tags = AudioTagValues.FromFile(mediaPath);
        var lufs = await AudioNormalizationTask.CalculateLUFSAsync(
            _services.Encoder.EncoderPath,
            string.Format(CultureInfo.InvariantCulture, "-i \"{0}\"", mediaPath.EscapeProcessArgument()),
            false,
            _logger,
            cancellationToken).ConfigureAwait(false) ?? throw new InvalidOperationException("ffmpeg reported no loudness.");

        var hasLyrics = !string.IsNullOrWhiteSpace(tags.Lyrics);
        if (hasLyrics)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, TrueCacheLayout.LyricsFile), tags.Lyrics, cancellationToken).ConfigureAwait(false);
        }

        var imageStreams = info.MediaStreams.Where(s => s.Type == MediaStreamType.EmbeddedImage).ToList();
        if (TrueCacheRules.FindAudioImage(imageStreams) is { } cover)
        {
            var image = await _services.Encoder.ExtractAudioImage(mediaPath, cover.Index, cancellationToken).ConfigureAwait(false);
            MoveInto(image, Path.Combine(directory, TrueCacheLayout.ImagesFolder, "primary.jpg"));
        }

        root["audio"] = new TomlTable
        {
            ["lufs"] = (double)lufs,
            ["embedded_lyrics"] = hasLyrics,
            ["tags"] = AudioTagTable(tags)
        };
    }

    private static TomlTable AudioTagTable(AudioTagValues tags)
    {
        var table = new TomlTable();
        void Text(string key, string? value)
        {
            if (value is not null)
            {
                table[key] = value;
            }
        }

        void List(string key, string? value)
        {
            if (!string.IsNullOrEmpty(value))
            {
                var array = new TomlArray();
                foreach (var item in value.Split(TrueCacheAudioTags.ValueSeparator))
                {
                    array.Add(item);
                }

                table[key] = array;
            }
        }

        void Number(string key, int? value)
        {
            if (value.HasValue)
            {
                table[key] = (long)value.Value;
            }
        }

        Text("title", tags.Title);
        Text("album", tags.Album);
        List("artists", tags.Artist);
        List("album_artists", tags.AlbumArtist);
        List("composers", tags.Composer);
        List("genres", tags.Genre);
        Number("year", tags.Year);
        Number("track", tags.TrackNumber);
        Number("disc", tags.DiscNumber);
        if (tags.Date.HasValue)
        {
            table["date"] = new TomlDateTime(new DateTimeOffset(DateTime.SpecifyKind(tags.Date.Value, DateTimeKind.Utc)), 0, TomlDateTimeKind.OffsetDateTimeByZ);
        }

        Text("description", tags.Description);
        Text("comment", tags.Comment);
        Text("publisher", tags.Publisher);
        table["id3v1_only"] = tags.Id3v1Only;

        var additional = new TomlTable();
        foreach (var (key, value) in tags.AdditionalFields)
        {
            additional[key] = value;
        }

        table["additional"] = additional;
        return table;
    }

    private async Task WriteBookAsync(string resourcePath, string directory, TomlTable root, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(resourcePath).ToLowerInvariant();
        var book = new TomlTable();
        var bookFolder = Path.Combine(directory, TrueCacheLayout.BookFolder);
        Directory.CreateDirectory(bookFolder);
        var item = new Book { Path = resourcePath };

        if (_comicArchiveExtensions.Contains(extension))
        {
            // Same count and cover as the probe provider and the comic cover extractor.
            using (var stream = File.OpenRead(resourcePath))
            using (var archive = ArchiveFactory.OpenArchive(stream))
            {
                book["page_count"] = (long)archive.Entries.Count(e => !e.IsDirectory);
            }

            var cover = await new ComicImageProvider(_services.LoggerFactory.CreateLogger<ComicImageProvider>(), NoTrueCacheService.Instance)
                .GetImage(item, ImageType.Primary, cancellationToken).ConfigureAwait(false);
            book["cover"] = await WriteCoverAsync(bookFolder, cover, cancellationToken).ConfigureAwait(false);

            if (extension == ".cbz")
            {
                using var zip = await ZipFile.OpenReadAsync(resourcePath, cancellationToken).ConfigureAwait(false);
                var comicInfo = zip.GetEntry(MediaBrowser.Providers.Books.ComicInfo.ComicInfoReader.ComicRackMetaFile);
                book["comic_info"] = comicInfo is not null;
                if (comicInfo is not null)
                {
                    await comicInfo.ExtractToFileAsync(Path.Combine(bookFolder, TrueCacheLayout.ComicInfoFile), true, cancellationToken).ConfigureAwait(false);
                }

                var hasComment = !string.IsNullOrWhiteSpace(zip.Comment);
                book["comic_book_info"] = hasComment;
                if (hasComment)
                {
                    await File.WriteAllTextAsync(Path.Combine(bookFolder, TrueCacheLayout.ComicBookInfoFile), zip.Comment, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        else if (extension == ".pdf")
        {
#pragma warning disable CA1416 // PDFtoImage supports the platforms the server supports.
            using var stream = File.OpenRead(resourcePath);
            book["page_count"] = (long)Conversion.GetPageCount(stream);
#pragma warning restore CA1416
        }
        else if (extension == ".epub")
        {
            using (var epub = await ZipFile.OpenReadAsync(resourcePath, cancellationToken).ConfigureAwait(false))
            {
                var opfPath = EpubUtils.ReadContentFilePath(epub);
                var opf = opfPath is null ? null : epub.GetEntry(opfPath);
                book["opf"] = opf is not null;
                if (opf is not null)
                {
                    await opf.ExtractToFileAsync(Path.Combine(bookFolder, TrueCacheLayout.OpfFile), true, cancellationToken).ConfigureAwait(false);
                }
            }

            var cover = await new EpubImageProvider(_services.LoggerFactory.CreateLogger<EpubImageProvider>(), NoTrueCacheService.Instance)
                .GetImage(item, ImageType.Primary, cancellationToken).ConfigureAwait(false);
            book["cover"] = await WriteCoverAsync(bookFolder, cover, cancellationToken).ConfigureAwait(false);
        }

        root["book"] = book;
    }

    private static async Task<bool> WriteCoverAsync(string bookFolder, MediaBrowser.Controller.Providers.DynamicImageResponse cover, CancellationToken cancellationToken)
    {
        if (!cover.HasImage || cover.Stream is null)
        {
            return false;
        }

        await using (cover.Stream.ConfigureAwait(false))
        {
            var target = File.Create(Path.Combine(bookFolder, TrueCacheLayout.CoverName + cover.Format.GetExtension()));
            await using (target.ConfigureAwait(false))
            {
                await cover.Stream.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
            }
        }

        return true;
    }
}
