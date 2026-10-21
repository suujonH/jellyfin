using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Text;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using Jellyfin.Extensions.Json.Converters;
using Jellyfin.MediaEncoding.Keyframes;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.MediaEncoding.Probing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace MediaBrowser.MediaEncoding.TrueCache;

/// <summary>
/// Loads a <c>__true_cache__</c> entry and checks that it holds everything the server could extract from its resource.
/// Only the entry is read; the resource itself is only looked at with <c>stat</c>.
/// </summary>
public sealed class TrueCacheLoader
{
    private const string StrmExtension = ".strm";

    private static readonly string[] _comicArchiveExtensions = [".cbz", ".cbr", ".cb7", ".cbt"];
    private static readonly TimeSpan _modifiedTolerance = TimeSpan.FromSeconds(2);

    // Documents the providers import from but the loader does not parse. An edit that keeps their size must still change the hash.
    private static readonly string[] _importedDocuments =
    [
        TrueCacheLayout.LyricsFile,
        TrueCacheLayout.BookFolder + "/" + TrueCacheLayout.ComicInfoFile,
        TrueCacheLayout.BookFolder + "/" + TrueCacheLayout.ComicBookInfoFile,
        TrueCacheLayout.BookFolder + "/" + TrueCacheLayout.OpfFile
    ];

    private readonly ProbeResultNormalizer _normalizer;
    private readonly JsonSerializerOptions _probeOptions;
    private readonly StringComparer _pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrueCacheLoader"/> class.
    /// </summary>
    /// <param name="logger">The logger used by the probe normalizer.</param>
    /// <param name="localization">The localization used by the probe normalizer.</param>
    public TrueCacheLoader(ILogger logger, ILocalizationManager localization)
    {
        _normalizer = new ProbeResultNormalizer(logger, localization);
        _probeOptions = new JsonSerializerOptions(JsonDefaults.Options);
        _probeOptions.Converters.Add(new JsonBoolStringConverter());
    }

    /// <summary>
    /// Loads the entry of a resource.
    /// </summary>
    /// <param name="resourcePath">The media file or disc folder.</param>
    /// <param name="requirements">The settings that decide what the entry must hold.</param>
    /// <returns>The lookup result.</returns>
    public TrueCacheLookup Load(string resourcePath, TrueCacheRequirements requirements)
    {
        var directory = TrueCacheLayout.GetEntryDirectory(resourcePath);
        if (directory is null || !Directory.Exists(directory))
        {
            return TrueCacheLookup.None;
        }

        return Load(resourcePath, directory, requirements);
    }

    /// <summary>
    /// Loads an entry from a folder as if it were the entry of a resource. The generator validates an entry this way
    /// before it puts the entry in place.
    /// </summary>
    /// <param name="resourcePath">The media file or disc folder.</param>
    /// <param name="directory">The folder holding the entry.</param>
    /// <param name="requirements">The settings that decide what the entry must hold.</param>
    /// <returns>The lookup result.</returns>
    public TrueCacheLookup Load(string resourcePath, string directory, TrueCacheRequirements requirements)
    {
        try
        {
            return LoadEntry(resourcePath, directory, requirements);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            return TrueCacheLookup.Rejected(ex.Message);
        }
    }

    /// <summary>
    /// Loads the album-wide entry of an album folder.
    /// </summary>
    /// <param name="albumPath">The album folder.</param>
    /// <returns>The lookup result.</returns>
    public TrueCacheLookup LoadAlbum(string albumPath)
    {
        var directory = TrueCacheLayout.GetAlbumEntryDirectory(albumPath);
        if (!Directory.Exists(directory))
        {
            return TrueCacheLookup.None;
        }

        return LoadAlbum(albumPath, directory);
    }

    /// <summary>
    /// Loads an album-wide entry from a folder as if it were the entry of an album folder.
    /// </summary>
    /// <param name="albumPath">The album folder.</param>
    /// <param name="directory">The folder holding the entry.</param>
    /// <returns>The lookup result.</returns>
    public TrueCacheLookup LoadAlbum(string albumPath, string directory)
    {
        try
        {
            var hash = new XxHash3();
            var manifest = ReadManifest(directory, hash);
            if (manifest.Kind != TrueCacheKind.Album)
            {
                return TrueCacheLookup.Rejected("an album entry must have kind = \"album\"");
            }

            if (!manifest.AlbumLufs.HasValue)
            {
                return TrueCacheLookup.Rejected("missing album.lufs");
            }

            return TrueCacheLookup.Valid(new TrueCacheEntry(albumPath, directory, ToHashString(hash), manifest, null, null, null, new Dictionary<ImageType, string>(), new Dictionary<int, string>()));
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            return TrueCacheLookup.Rejected(ex.Message);
        }
    }

    private static TrueCacheManifest ReadManifest(string directory, XxHash3 hash)
    {
        if (!File.Exists(Path.Combine(directory, TrueCacheLayout.MetaFile)))
        {
            throw new FormatException("missing " + TrueCacheLayout.MetaFile);
        }

        return TrueCacheManifestReader.Read(ReadText(directory, TrueCacheLayout.MetaFile, hash));
    }

    /// <summary>
    /// Reads a file of an entry the way <see cref="File.ReadAllText(string)"/> does, and adds the file to the hash of the entry.
    /// </summary>
    private static string ReadText(string directory, string relative, XxHash3 hash)
    {
        using var reader = new StreamReader(new MemoryStream(AppendContents(hash, directory, relative)));
        return reader.ReadToEnd();
    }

    private static byte[] AppendContents(XxHash3 hash, string directory, string relative)
    {
        var bytes = File.ReadAllBytes(ToPath(directory, relative));
        AppendFile(hash, relative, bytes.LongLength);
        hash.Append(bytes);
        return bytes;
    }

    private static void AppendFile(XxHash3 hash, string relative, long length)
    {
        Span<byte> size = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64LittleEndian(size, length);
        hash.Append(Encoding.UTF8.GetBytes(relative));
        hash.Append("\0"u8);
        hash.Append(size);
    }

    private static string ToHashString(XxHash3 hash) => Convert.ToHexStringLower(hash.GetCurrentHash());

    private static string Relative(params string[] parts) => string.Join('/', parts);

    private static string ToPath(string directory, string relative) => Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));

    private TrueCacheLookup LoadEntry(string resourcePath, string directory, TrueCacheRequirements requirements)
    {
        var hash = new XxHash3();
        var manifest = ReadManifest(directory, hash);
        var missing = new List<string>();

        var isFolder = Directory.Exists(resourcePath);
        if (!isFolder && !File.Exists(resourcePath))
        {
            return TrueCacheLookup.Rejected("the resource does not exist");
        }

        var expectsFolder = manifest.Kind is TrueCacheKind.Dvd or TrueCacheKind.BluRay;
        if (manifest.Kind == TrueCacheKind.Album || expectsFolder != isFolder)
        {
            return TrueCacheLookup.Rejected($"kind {manifest.Kind} does not describe a {(isFolder ? "folder" : "file")}");
        }

        if (!isFolder)
        {
            CheckSource(resourcePath, manifest, missing);
        }

        var listing = new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(f => (Name: Path.GetRelativePath(directory, f.FullName).Replace(Path.DirectorySeparatorChar, '/'), f.Length))
            .ToList();
        var files = new HashSet<string>(listing.Select(f => f.Name), _pathComparer);

        var images = new Dictionary<ImageType, string>();
        var subtitles = new Dictionary<int, string>();
        MediaInfo? mediaInfo = null;
        BlurayDiscInfo? bluRay = null;
        KeyframeData? keyframes = null;

        switch (manifest.Kind)
        {
            case TrueCacheKind.Video:
            case TrueCacheKind.Dvd:
            case TrueCacheKind.BluRay:
                mediaInfo = ReadProbe(directory, files, resourcePath, manifest.Kind, hash, missing);
                if (mediaInfo is not null)
                {
                    (bluRay, keyframes) = CheckVideo(resourcePath, directory, files, manifest, mediaInfo, requirements, hash, images, subtitles, missing);
                }

                break;
            case TrueCacheKind.Audio:
                mediaInfo = ReadProbe(directory, files, resourcePath, manifest.Kind, hash, missing);
                if (mediaInfo is not null)
                {
                    CheckAudio(directory, files, manifest, mediaInfo, images, missing);
                }

                break;
            case TrueCacheKind.Book:
                CheckBook(resourcePath, files, manifest, missing);
                break;
            case TrueCacheKind.Photo:
                if (manifest.Photo is null || manifest.Photo.Width <= 0 || manifest.Photo.Height <= 0)
                {
                    missing.Add("photo.width and photo.height");
                }

                break;
        }

        if (missing.Count > 0)
        {
            return TrueCacheLookup.Rejected("missing " + string.Join(", ", missing));
        }

        foreach (var document in _importedDocuments.Where(files.Contains))
        {
            AppendContents(hash, directory, document);
        }

        // The listing covers the files the server does not read, such as images and trickplay tiles.
        foreach (var (name, length) in listing.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            AppendFile(hash, name, length);
        }

        return TrueCacheLookup.Valid(new TrueCacheEntry(resourcePath, directory, ToHashString(hash), manifest, mediaInfo, bluRay, keyframes, images, subtitles));
    }

    private static void CheckSource(string resourcePath, TrueCacheManifest manifest, List<string> missing)
    {
        var info = new FileInfo(resourcePath);
        if (!manifest.SourceSize.HasValue)
        {
            missing.Add("source.size");
        }
        else if (manifest.SourceSize.Value != info.Length)
        {
            throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"source.size {manifest.SourceSize.Value} does not match the resource ({info.Length})"));
        }

        if (manifest.SourceModified.HasValue
            && (info.LastWriteTimeUtc - DateTime.SpecifyKind(manifest.SourceModified.Value, DateTimeKind.Utc)).Duration() > _modifiedTolerance)
        {
            throw new FormatException("source.modified does not match the resource");
        }
    }

    private MediaInfo? ReadProbe(string directory, HashSet<string> files, string resourcePath, TrueCacheKind kind, XxHash3 hash, List<string> missing)
    {
        if (!files.Contains(TrueCacheLayout.ProbeFile))
        {
            missing.Add(TrueCacheLayout.ProbeFile);
            return null;
        }

        return NormalizeProbe(ReadText(directory, TrueCacheLayout.ProbeFile, hash), resourcePath, kind);
    }

    /// <summary>
    /// Turns a stored ffprobe output into the probe result the server would get from a live probe.
    /// </summary>
    /// <param name="json">The ffprobe output.</param>
    /// <param name="resourcePath">The resource it describes.</param>
    /// <param name="kind">The kind of resource.</param>
    /// <returns>The probe result.</returns>
    /// <exception cref="FormatException">The output holds neither streams nor a format.</exception>
    public MediaInfo NormalizeProbe(string json, string resourcePath, TrueCacheKind kind)
    {
        var result = JsonSerializer.Deserialize<InternalMediaInfoResult>(json, _probeOptions);
        if (result is null || (result.Streams is null && result.Format is null))
        {
            throw new FormatException(TrueCacheLayout.ProbeFile + " has neither streams nor format");
        }

        if (result.Streams is not null)
        {
            // Same normalization as a live probe.
            foreach (var stream in result.Streams)
            {
                if (string.Equals(stream.DisplayAspectRatio, "0:1", StringComparison.OrdinalIgnoreCase))
                {
                    stream.DisplayAspectRatio = string.Empty;
                }

                if (string.Equals(stream.SampleAspectRatio, "0:1", StringComparison.OrdinalIgnoreCase))
                {
                    stream.SampleAspectRatio = string.Empty;
                }
            }
        }

        var videoType = kind switch
        {
            TrueCacheKind.Dvd => VideoType.Dvd,
            TrueCacheKind.BluRay => VideoType.BluRay,
            TrueCacheKind.Video => VideoType.VideoFile,
            _ => (VideoType?)null
        };
        var protocol = resourcePath.EndsWith(StrmExtension, StringComparison.OrdinalIgnoreCase) ? MediaProtocol.Http : MediaProtocol.File;
        return _normalizer.GetMediaInfo(result, videoType, kind == TrueCacheKind.Audio, resourcePath, protocol, false);
    }

    private (BlurayDiscInfo? BluRay, KeyframeData? Keyframes) CheckVideo(
        string resourcePath,
        string directory,
        HashSet<string> files,
        TrueCacheManifest manifest,
        MediaInfo mediaInfo,
        TrueCacheRequirements requirements,
        XxHash3 hash,
        Dictionary<ImageType, string> images,
        Dictionary<int, string> subtitles,
        List<string> missing)
    {
        var kind = manifest.Kind;
        var video = manifest.Video;
        var isShortcut = kind == TrueCacheKind.Video && resourcePath.EndsWith(StrmExtension, StringComparison.OrdinalIgnoreCase);
        BlurayDiscInfo? bluRay = null;
        KeyframeData? keyframes = null;

        if (kind == TrueCacheKind.Video && !isShortcut && TrueCacheRules.TimestampContainers.Contains(mediaInfo.Container, StringComparer.OrdinalIgnoreCase))
        {
            if (video?.Timestamp is null)
            {
                missing.Add("video.timestamp");
            }
            else
            {
                mediaInfo.Timestamp = video.Timestamp;
            }
        }

        if (kind == TrueCacheKind.Dvd)
        {
            if (video?.RuntimeTicks is null)
            {
                missing.Add("video.runtime_ticks");
            }
            else
            {
                mediaInfo.RunTimeTicks = video.RuntimeTicks;
            }
        }

        if (kind == TrueCacheKind.BluRay)
        {
            bluRay = ReadBluRay(resourcePath, directory, files, hash, missing);
        }

        var streams = mediaInfo.MediaStreams;
        var hasVideo = streams.Any(s => s.Type == MediaStreamType.Video);
        var runtime = bluRay?.RunTimeTicks is > 0 ? bluRay.RunTimeTicks.Value : mediaInfo.RunTimeTicks ?? 0;
        var extractsFromFile = !isShortcut;

        if (hasVideo && extractsFromFile && kind != TrueCacheKind.Dvd && !files.Contains(TrueCacheLayout.ScreenGrabFile))
        {
            missing.Add(TrueCacheLayout.ScreenGrabFile);
        }

        if (extractsFromFile && kind != TrueCacheKind.Dvd)
        {
            var imageStreams = streams.Where(s => s.Type == MediaStreamType.EmbeddedImage).ToList();
            foreach (var type in TrueCacheRules.EmbeddedImageTypes)
            {
                var (attachment, stream, _) = TrueCacheRules.FindEmbeddedImage(type, mediaInfo.MediaAttachments, imageStreams);
                if (attachment is not null || stream is not null)
                {
                    RequireImage(directory, files, type, images, missing);
                }
            }
        }

        if (hasVideo && extractsFromFile)
        {
            var chapters = TrueCacheRules.GetVideoChapters(mediaInfo.Chapters, bluRay?.Chapters, runtime, requirements.DummyChapterDuration);
            foreach (var chapter in TrueCacheRules.GetChaptersWithImages(chapters, runtime))
            {
                var name = Relative(TrueCacheLayout.ChaptersFolder, chapter.StartPositionTicks.ToString(CultureInfo.InvariantCulture) + ".jpg");
                if (!files.Contains(name))
                {
                    missing.Add(name);
                }
            }
        }

        if (kind == TrueCacheKind.Video && extractsFromFile && hasVideo && runtime >= TimeSpan.FromMilliseconds(requirements.TrickplayInterval).Ticks)
        {
            var videoWidth = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video)?.Width;
            foreach (var width in requirements.TrickplayWidths.Select(w => TrueCacheRules.GetTrickplayWidth(w, videoWidth)).Distinct())
            {
                if (video is null || !video.Trickplay.Any(t => t.Width == width))
                {
                    missing.Add(string.Create(CultureInfo.InvariantCulture, $"video.trickplay with width {width}"));
                }
            }
        }

        foreach (var trickplay in video?.Trickplay ?? [])
        {
            CheckTrickplay(files, trickplay, missing);
        }

        foreach (var stream in ((IReadOnlyList<MediaStream>?)bluRay?.MediaStreams ?? streams).Where(s => s.Type == MediaStreamType.Subtitle && !s.IsExternal && s.IsExtractableSubtitleStream))
        {
            var name = Relative(TrueCacheLayout.SubtitlesFolder, stream.Index.ToString(CultureInfo.InvariantCulture) + "." + TrueCacheRules.GetExtractedSubtitleExtension(stream.Codec));
            if (files.Contains(name))
            {
                subtitles[stream.Index] = ToPath(directory, name);
            }
            else
            {
                missing.Add(name);
            }
        }

        // The server never extracts mjpeg attachments; cover images among them are covered by the embedded images.
        foreach (var attachment in mediaInfo.MediaAttachments.Where(a => !string.Equals(a.Codec, "mjpeg", StringComparison.OrdinalIgnoreCase)))
        {
            var name = Relative(TrueCacheLayout.AttachmentsFolder, TrueCacheRules.GetAttachmentFileName(attachment));
            if (!files.Contains(name))
            {
                missing.Add(name);
            }
        }

        if (kind == TrueCacheKind.Video && extractsFromFile && hasVideo)
        {
            if (files.Contains(TrueCacheLayout.KeyframesFile))
            {
                keyframes = ReadKeyframes(directory, hash);
            }
            else
            {
                missing.Add(TrueCacheLayout.KeyframesFile);
            }
        }

        return (bluRay, keyframes);
    }

    private static void CheckTrickplay(HashSet<string> files, TrueCacheTrickplay trickplay, List<string> missing)
    {
        if (trickplay.Width <= 0 || trickplay.Height <= 0 || trickplay.TileWidth <= 0 || trickplay.TileHeight <= 0
            || trickplay.ThumbnailCount <= 0 || trickplay.Interval <= 0)
        {
            throw new FormatException(string.Create(CultureInfo.InvariantCulture, $"video.trickplay with width {trickplay.Width} has a zero or negative value"));
        }

        for (var i = 0; i < trickplay.TileCount; i++)
        {
            var name = Relative(TrueCacheLayout.TrickplayFolder, trickplay.FolderName, i.ToString(CultureInfo.InvariantCulture) + ".jpg");
            if (!files.Contains(name))
            {
                missing.Add(name);
                return;
            }
        }
    }

    private void RequireImage(string directory, HashSet<string> files, ImageType type, Dictionary<ImageType, string> images, List<string> missing)
    {
        var baseName = Relative(TrueCacheLayout.ImagesFolder, type.ToString().ToLowerInvariant());
        foreach (var extension in TrueCacheRules.ImageExtensions)
        {
            if (files.Contains(baseName + extension))
            {
                images[type] = ToPath(directory, baseName + extension);
                return;
            }
        }

        missing.Add(baseName + ".*");
    }

    private BlurayDiscInfo? ReadBluRay(string resourcePath, string directory, HashSet<string> files, XxHash3 hash, List<string> missing)
    {
        if (!files.Contains(TrueCacheLayout.BdInfoFile))
        {
            missing.Add(TrueCacheLayout.BdInfoFile);
            return null;
        }

        var info = JsonSerializer.Deserialize<BlurayDiscInfo>(ReadText(directory, TrueCacheLayout.BdInfoFile, hash), JsonDefaults.Options)
            ?? throw new FormatException(TrueCacheLayout.BdInfoFile + " is empty");
        if (info.Files is not { Length: > 0 })
        {
            throw new FormatException(TrueCacheLayout.BdInfoFile + " lists no playable files");
        }

        // The stored file list is relative to the disc folder, so the entry stays valid wherever the disc is mounted.
        info.Files = info.Files.Select(f => Path.GetFullPath(Path.Combine(resourcePath, f))).ToArray();
        info.MediaStreams ??= [];
        return info;
    }

    private KeyframeData ReadKeyframes(string directory, XxHash3 hash)
    {
        var data = JsonSerializer.Deserialize<KeyframeData>(ReadText(directory, TrueCacheLayout.KeyframesFile, hash), JsonDefaults.Options)
            ?? throw new FormatException(TrueCacheLayout.KeyframesFile + " is empty");
        if (data.KeyframeTicks is null || data.TotalDuration <= 0)
        {
            throw new FormatException(TrueCacheLayout.KeyframesFile + " needs TotalDuration and KeyframeTicks");
        }

        return data;
    }

    private static void CheckAudio(string directory, HashSet<string> files, TrueCacheManifest manifest, MediaInfo mediaInfo, Dictionary<ImageType, string> images, List<string> missing)
    {
        var audio = manifest.Audio;
        if (audio?.Tags is null)
        {
            missing.Add("audio.tags");
        }

        if (audio?.Lufs is null)
        {
            missing.Add("audio.lufs");
        }

        if (audio?.EmbeddedLyrics is null)
        {
            missing.Add("audio.embedded_lyrics");
        }
        else if (audio.EmbeddedLyrics.Value && !files.Contains(TrueCacheLayout.LyricsFile))
        {
            missing.Add(TrueCacheLayout.LyricsFile);
        }

        if (mediaInfo.MediaStreams.Any(s => s.Type == MediaStreamType.EmbeddedImage))
        {
            var baseName = Relative(TrueCacheLayout.ImagesFolder, "primary");
            var found = TrueCacheRules.ImageExtensions.FirstOrDefault(e => files.Contains(baseName + e));
            if (found is null)
            {
                missing.Add(baseName + ".*");
            }
            else
            {
                images[ImageType.Primary] = ToPath(directory, baseName + found);
            }
        }
    }

    private static void CheckBook(string resourcePath, HashSet<string> files, TrueCacheManifest manifest, List<string> missing)
    {
        var extension = Path.GetExtension(resourcePath).ToLowerInvariant();
        var book = manifest.Book;
        var isComicArchive = _comicArchiveExtensions.Contains(extension);
        var isEpub = extension == ".epub";
        if (!isComicArchive && !isEpub && extension != ".pdf")
        {
            return;
        }

        if (book is null)
        {
            missing.Add("[book]");
            return;
        }

        if ((isComicArchive || extension == ".pdf") && !book.PageCount.HasValue)
        {
            missing.Add("book.page_count");
        }

        if (extension == ".cbz")
        {
            RequireDocument(files, book.ComicInfo, "book.comic_info", TrueCacheLayout.ComicInfoFile, missing);
            RequireDocument(files, book.ComicBookInfo, "book.comic_book_info", TrueCacheLayout.ComicBookInfoFile, missing);
        }

        if (isEpub)
        {
            RequireDocument(files, book.Opf, "book.opf", TrueCacheLayout.OpfFile, missing);
        }

        if (isComicArchive || isEpub)
        {
            if (!book.Cover.HasValue)
            {
                missing.Add("book.cover");
            }
            else if (book.Cover.Value && !files.Any(f => f.StartsWith(TrueCacheLayout.BookFolder + "/" + TrueCacheLayout.CoverName + ".", StringComparison.OrdinalIgnoreCase)))
            {
                missing.Add(Relative(TrueCacheLayout.BookFolder, TrueCacheLayout.CoverName + ".*"));
            }
        }
    }

    private static void RequireDocument(HashSet<string> files, bool? declared, string key, string fileName, List<string> missing)
    {
        if (!declared.HasValue)
        {
            missing.Add(key);
        }
        else if (declared.Value && !files.Contains(Relative(TrueCacheLayout.BookFolder, fileName)))
        {
            missing.Add(Relative(TrueCacheLayout.BookFolder, fileName));
        }
    }
}
