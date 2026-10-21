using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Jellyfin.Extensions.Json;
using Jellyfin.MediaEncoding.Keyframes;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.MediaInfo;

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// A complete <c>__true_cache__</c> entry: everything the server would otherwise learn by reading the resource.
/// </summary>
public sealed class TrueCacheEntry
{
    private readonly IReadOnlyDictionary<ImageType, string> _embeddedImages;
    private readonly IReadOnlyDictionary<int, string> _subtitles;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrueCacheEntry"/> class.
    /// </summary>
    /// <param name="resourcePath">The resource the entry describes.</param>
    /// <param name="directory">The entry folder.</param>
    /// <param name="hash">The hash of the entry.</param>
    /// <param name="manifest">The declaration.</param>
    /// <param name="mediaInfo">The normalized probe result, for media resources.</param>
    /// <param name="bluRay">The Blu-ray structure, for Blu-ray folders.</param>
    /// <param name="keyframes">The keyframes, for videos that have them.</param>
    /// <param name="embeddedImages">The embedded images by image type.</param>
    /// <param name="subtitles">The extracted embedded subtitles by stream index.</param>
    public TrueCacheEntry(
        string resourcePath,
        string directory,
        string hash,
        TrueCacheManifest manifest,
        MediaInfo? mediaInfo,
        BlurayDiscInfo? bluRay,
        KeyframeData? keyframes,
        IReadOnlyDictionary<ImageType, string> embeddedImages,
        IReadOnlyDictionary<int, string> subtitles)
    {
        ResourcePath = resourcePath;
        Directory = directory;
        Hash = hash;
        Manifest = manifest;
        MediaInfo = mediaInfo;
        BluRay = bluRay;
        Keyframes = keyframes;
        _embeddedImages = embeddedImages;
        _subtitles = subtitles;
    }

    /// <summary>Gets the resource the entry describes.</summary>
    public string ResourcePath { get; }

    /// <summary>Gets the entry folder.</summary>
    public string Directory { get; }

    /// <summary>
    /// Gets the hash of the entry: the contents of the files the server reads, and the names and sizes of all files.
    /// Any edit or replacement of the entry changes it, whatever the modification times are.
    /// </summary>
    public string Hash { get; }

    /// <summary>Gets the declaration.</summary>
    public TrueCacheManifest Manifest { get; }

    /// <summary>Gets the normalized probe result; <c>null</c> for books and photos.</summary>
    public MediaInfo? MediaInfo { get; }

    /// <summary>
    /// Gets the size of the media the entry describes. The declared size of a .strm is the size of its target, not of the
    /// text file, and it is what counts; otherwise the probed size, or the declared size of the resource without one.
    /// </summary>
    public long? MediaSize => TrueCacheLayout.IsShortcut(ResourcePath)
        ? Manifest.SourceSize ?? MediaInfo?.Size
        : MediaInfo?.Size ?? Manifest.SourceSize;

    /// <summary>Gets the Blu-ray structure of a Blu-ray folder.</summary>
    public BlurayDiscInfo? BluRay { get; }

    /// <summary>Gets the keyframes of a video.</summary>
    public KeyframeData? Keyframes { get; }

    /// <summary>Gets the screen grab of a video.</summary>
    public string ScreenGrabPath => Path.Combine(Directory, TrueCacheLayout.ScreenGrabFile);

    /// <summary>Gets the folder that holds the trickplay resolutions.</summary>
    public string TrickplayDirectory => Path.Combine(Directory, TrueCacheLayout.TrickplayFolder);

    /// <summary>Gets the embedded lyrics of an audio file.</summary>
    public string LyricsPath => Path.Combine(Directory, TrueCacheLayout.LyricsFile);

    /// <summary>
    /// Creates a copy of the probe result that a caller may change.
    /// </summary>
    /// <param name="includeChapters">Whether the caller asked for chapters.</param>
    /// <returns>The probe result.</returns>
    /// <exception cref="InvalidOperationException">The entry describes no media.</exception>
    public MediaInfo CreateMediaInfo(bool includeChapters)
    {
        var source = MediaInfo ?? throw new InvalidOperationException("The entry of " + ResourcePath + " has no probe result.");
        var copy = JsonSerializer.Deserialize<MediaInfo>(JsonSerializer.SerializeToUtf8Bytes(source, JsonDefaults.Options), JsonDefaults.Options)!;
        if (!includeChapters)
        {
            copy.Chapters = [];
        }

        return copy;
    }

    /// <summary>
    /// Creates a copy of the Blu-ray structure that a caller may change.
    /// </summary>
    /// <returns>The structure, or <c>null</c> for resources other than Blu-ray folders.</returns>
    public BlurayDiscInfo? CreateBluRayInfo()
        => BluRay is null
            ? null
            : JsonSerializer.Deserialize<BlurayDiscInfo>(JsonSerializer.SerializeToUtf8Bytes(BluRay, JsonDefaults.Options), JsonDefaults.Options);

    /// <summary>
    /// Gets the extracted embedded image of a type.
    /// </summary>
    /// <param name="type">The image type.</param>
    /// <param name="path">The image file.</param>
    /// <returns><c>true</c> when the resource has an embedded image of that type.</returns>
    public bool TryGetEmbeddedImage(ImageType type, out string? path) => _embeddedImages.TryGetValue(type, out path);

    /// <summary>
    /// Gets the image of a chapter.
    /// </summary>
    /// <param name="startPositionTicks">The start of the chapter.</param>
    /// <returns>The image file.</returns>
    public string GetChapterImagePath(long startPositionTicks)
        => Path.Combine(Directory, TrueCacheLayout.ChaptersFolder, startPositionTicks.ToString(CultureInfo.InvariantCulture) + ".jpg");

    /// <summary>
    /// Gets the extracted subtitle of an embedded stream.
    /// </summary>
    /// <param name="streamIndex">The index of the stream in the probe result.</param>
    /// <param name="path">The subtitle file.</param>
    /// <returns><c>true</c> when the stream has been extracted.</returns>
    public bool TryGetSubtitle(int streamIndex, out string? path) => _subtitles.TryGetValue(streamIndex, out path);

    /// <summary>
    /// Gets the folder with the extracted attachments.
    /// </summary>
    /// <returns>The folder.</returns>
    public string GetAttachmentsDirectory() => Path.Combine(Directory, TrueCacheLayout.AttachmentsFolder);

    /// <summary>
    /// Gets a document of a book.
    /// </summary>
    /// <param name="fileName">One of the book file names of <see cref="TrueCacheLayout"/>.</param>
    /// <returns>The document file.</returns>
    public string GetBookFile(string fileName) => Path.Combine(Directory, TrueCacheLayout.BookFolder, fileName);

    /// <summary>
    /// Gets the cover of a book.
    /// </summary>
    /// <returns>The cover file, or <c>null</c> when the book has none.</returns>
    public string? GetBookCover()
    {
        if (Manifest.Book?.Cover != true)
        {
            return null;
        }

        var folder = Path.Combine(Directory, TrueCacheLayout.BookFolder);
        foreach (var file in System.IO.Directory.EnumerateFiles(folder, TrueCacheLayout.CoverName + ".*"))
        {
            return file;
        }

        return null;
    }

    /// <summary>
    /// Gets the subtitle stream of the probe result that corresponds to an embedded stream of a media source.
    /// The server can drop embedded subtitle streams and renumber the rest, so streams are matched in order.
    /// </summary>
    /// <param name="sourceStreams">The streams of the media source.</param>
    /// <param name="stream">An embedded subtitle stream of the media source.</param>
    /// <param name="localization">The localization manager, which maps language codes as the library does.</param>
    /// <returns>The probe stream index, or <c>null</c> when there is no match.</returns>
    public int? FindProbeSubtitleIndex(IReadOnlyList<MediaStream> sourceStreams, MediaStream stream, ILocalizationManager localization)
    {
        if (MediaInfo is null)
        {
            return null;
        }

        // The server stores the BDInfo streams of a Blu-ray folder, and the probed streams otherwise.
        var probeStreams = (IReadOnlyList<MediaStream>?)BluRay?.MediaStreams ?? MediaInfo.MediaStreams;
        var probeIndex = 0;
        foreach (var current in sourceStreams)
        {
            if (current.IsExternal || current.Type != MediaStreamType.Subtitle)
            {
                continue;
            }

            var match = probeIndex;
            while (match < probeStreams.Count && !Matches(probeStreams[match], current, localization))
            {
                match++;
            }

            if (ReferenceEquals(current, stream))
            {
                return match < probeStreams.Count ? probeStreams[match].Index : null;
            }

            // A stream without a counterpart does not use up the probe streams that later streams match.
            if (match < probeStreams.Count)
            {
                probeIndex = match + 1;
            }
        }

        return null;
    }

    private static bool Matches(MediaStream probe, MediaStream current, ILocalizationManager localization)
        => probe.Type == MediaStreamType.Subtitle
           && string.Equals(probe.Codec, current.Codec, StringComparison.OrdinalIgnoreCase)
           && string.Equals(NormalizeLanguage(probe.Language, localization), NormalizeLanguage(current.Language, localization), StringComparison.OrdinalIgnoreCase)
           && string.Equals(probe.Title, current.Title, StringComparison.Ordinal)
           && probe.IsDefault == current.IsDefault
           && probe.IsForced == current.IsForced;

    // The probe keeps the ISO 639-2/B code it read, while the library returns stored streams with the /T code; both are compared as /T.
    private static string? NormalizeLanguage(string? language, ILocalizationManager localization)
        => language is not null && localization.TryGetISO6392TFromB(language, out var isoT) ? isoT : language;
}
