using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Jellyfin.Extensions;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Net;

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// Decisions the server makes about what it extracts from a resource. The server, the validation of
/// <c>__true_cache__</c> entries and the generator all use these, so an entry holds exactly what the server would extract.
/// </summary>
public static class TrueCacheRules
{
    private static readonly string[] _primaryImageNames = ["poster", "folder", "cover", "default", "movie", "show"];
    private static readonly string[] _backdropImageNames = ["backdrop", "background", "art"];
    private static readonly string[] _logoImageNames = ["logo"];

    /// <summary>Gets the image types the embedded image extractor provides for videos.</summary>
    public static IReadOnlyList<ImageType> EmbeddedImageTypes { get; } = [ImageType.Primary, ImageType.Backdrop, ImageType.Logo];

    /// <summary>Gets the extensions an extracted embedded image may have.</summary>
    public static IReadOnlyList<string> ImageExtensions { get; } = [".jpg", ".png", ".gif", ".bmp", ".webp"];

    /// <summary>Gets the containers whose transport stream timestamp the server reads from the file.</summary>
    public static IReadOnlyList<string> TimestampContainers { get; } = ["mpeg2ts", "m2ts", "ts"];

    /// <summary>
    /// Finds the embedded image the embedded image extractor uses for an image type.
    /// </summary>
    /// <param name="type">The image type.</param>
    /// <param name="attachments">The attachments of the video.</param>
    /// <param name="imageStreams">The <see cref="MediaStreamType.EmbeddedImage"/> streams of the video.</param>
    /// <returns>The attachment or stream holding the image, and the format of the extracted image; both <c>null</c> when there is none.</returns>
    public static (MediaAttachment? Attachment, MediaStream? Stream, ImageFormat Format) FindEmbeddedImage(
        ImageType type,
        IReadOnlyList<MediaAttachment> attachments,
        IReadOnlyList<MediaStream> imageStreams)
    {
        string[] names = type switch
        {
            ImageType.Primary => _primaryImageNames,
            ImageType.Backdrop => _backdropImageNames,
            ImageType.Logo => _logoImageNames,
            _ => []
        };

        if (names.Length == 0)
        {
            return (null, null, ImageFormat.Jpg);
        }

        var attachment = attachments.FirstOrDefault(a => !string.IsNullOrEmpty(a.FileName)
            && names.Any(name => a.FileName.Contains(name, StringComparison.OrdinalIgnoreCase)));
        if (attachment is not null)
        {
            var extension = string.IsNullOrEmpty(attachment.MimeType)
                ? Path.GetExtension(attachment.FileName)
                : MimeTypes.ToExtension(attachment.MimeType);
            var format = extension switch
            {
                ".bmp" => ImageFormat.Bmp,
                ".gif" => ImageFormat.Gif,
                ".png" => ImageFormat.Png,
                ".webp" => ImageFormat.Webp,
                _ => ImageFormat.Jpg
            };

            return (attachment, null, format);
        }

        if (imageStreams.Count == 0)
        {
            return (null, null, ImageFormat.Jpg);
        }

        var stream = imageStreams.FirstOrDefault(s => !string.IsNullOrEmpty(s.Comment)
            && names.Any(name => s.Comment.Contains(name, StringComparison.OrdinalIgnoreCase)));
        if (stream is null && type == ImageType.Primary)
        {
            stream = imageStreams[0];
        }

        if (stream is null)
        {
            return (null, null, ImageFormat.Jpg);
        }

        return (null, stream, GetEmbeddedImageFormat(stream.Codec));
    }

    /// <summary>
    /// Creates the provider response for an image of an entry. The image is handed over as a stream:
    /// the server deletes an image it receives by path after saving it.
    /// </summary>
    /// <param name="path">The image file.</param>
    /// <returns>The response.</returns>
    public static Providers.DynamicImageResponse CreateImageResponse(string path) => new()
    {
        HasImage = true,
        Stream = AsyncFile.OpenRead(path),
        Format = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => ImageFormat.Png,
            ".gif" => ImageFormat.Gif,
            ".bmp" => ImageFormat.Bmp,
            ".webp" => ImageFormat.Webp,
            ".svg" => ImageFormat.Svg,
            _ => ImageFormat.Jpg
        }
    };

    /// <summary>
    /// Gets the format of an image extracted from an embedded image stream.
    /// </summary>
    /// <param name="codec">The codec of the stream.</param>
    /// <returns>The image format.</returns>
    public static ImageFormat GetEmbeddedImageFormat(string? codec) => codec switch
    {
        "bmp" => ImageFormat.Bmp,
        "gif" => ImageFormat.Gif,
        "mjpeg" => ImageFormat.Jpg,
        "png" => ImageFormat.Png,
        "webp" => ImageFormat.Webp,
        _ => ImageFormat.Jpg
    };

    /// <summary>
    /// Finds the embedded image the audio image extractor uses.
    /// </summary>
    /// <param name="imageStreams">The <see cref="MediaStreamType.EmbeddedImage"/> streams of the audio file.</param>
    /// <returns>The stream, or <c>null</c> when there is none.</returns>
    public static MediaStream? FindAudioImage(IReadOnlyList<MediaStream> imageStreams)
        => imageStreams.FirstOrDefault(i => (i.Comment ?? string.Empty).Contains("front", StringComparison.OrdinalIgnoreCase))
           ?? imageStreams.FirstOrDefault(i => (i.Comment ?? string.Empty).Contains("cover", StringComparison.OrdinalIgnoreCase))
           ?? imageStreams.FirstOrDefault();

    /// <summary>
    /// Gets the format the server extracts an embedded subtitle stream to.
    /// </summary>
    /// <param name="codec">The codec of the stream.</param>
    /// <returns>The subtitle format.</returns>
    public static string GetExtractedSubtitleFormat(string codec)
    {
        if (string.Equals(codec, "ass", StringComparison.OrdinalIgnoreCase)
            || string.Equals(codec, "ssa", StringComparison.OrdinalIgnoreCase)
            || string.Equals(codec, "pgssub", StringComparison.OrdinalIgnoreCase))
        {
            return codec;
        }

        return MediaStream.IsVobSubFormat(codec) ? "mks" : "srt";
    }

    /// <summary>
    /// Gets the file extension, without dot, of an extracted embedded subtitle stream.
    /// </summary>
    /// <param name="codec">The codec of the stream.</param>
    /// <returns>The file extension.</returns>
    public static string GetExtractedSubtitleExtension(string codec)
    {
        // Using .pgssub as file extension is not allowed by ffmpeg. The file extension for pgs subtitles is .sup.
        if (string.Equals(codec, "pgssub", StringComparison.OrdinalIgnoreCase))
        {
            return "sup";
        }

        // FFmpeg cannot mux VobSub subtitle streams back into the .idx/.sub pair, so we use .mks container instead.
        return MediaStream.IsVobSubFormat(codec) ? "mks" : GetExtractedSubtitleFormat(codec);
    }

    /// <summary>
    /// Gets the file name the server extracts an attachment to.
    /// </summary>
    /// <param name="attachment">The attachment.</param>
    /// <returns>The file name.</returns>
    public static string GetAttachmentFileName(MediaAttachment attachment)
    {
        var indexName = attachment.Index.ToString(CultureInfo.InvariantCulture);
        return PathHelper.GetSafeLeafFileName(attachment.FileName ?? indexName) ?? indexName;
    }

    /// <summary>
    /// Gets the width the server generates trickplay thumbnails at for a configured width.
    /// </summary>
    /// <param name="configuredWidth">The configured width.</param>
    /// <param name="videoWidth">The width of the video stream.</param>
    /// <returns>The even width the thumbnails get; never wider than the video.</returns>
    public static int GetTrickplayWidth(int configuredWidth, int? videoWidth)
        => videoWidth is not null && videoWidth < configuredWidth
            ? 2 * (videoWidth.Value / 2)
            : 2 * (configuredWidth / 2);

    /// <summary>
    /// Creates the chapters the server generates for a video without chapters.
    /// </summary>
    /// <param name="runtimeTicks">The runtime of the video.</param>
    /// <param name="dummyChapterDurationSeconds">The configured duration of a generated chapter.</param>
    /// <returns>The chapters.</returns>
    /// <exception cref="ArgumentException">The runtime is invalid.</exception>
    public static ChapterInfo[] CreateDummyChapters(long runtimeTicks, int dummyChapterDurationSeconds)
    {
        // Only process files with a runtime greater than 0 and less than 12h. The latter are likely corrupted.
        if (runtimeTicks < 0 || runtimeTicks > TimeSpan.FromHours(12).Ticks)
        {
            throw new ArgumentException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "Invalid runtime of {0} minutes",
                    TimeSpan.FromTicks(runtimeTicks).TotalMinutes));
        }

        long dummyChapterDuration = TimeSpan.FromSeconds(dummyChapterDurationSeconds).Ticks;

        if (runtimeTicks <= 0)
        {
            return [];
        }

        int chapterCount = Math.Max(1, (int)(runtimeTicks / dummyChapterDuration));
        var chapters = new ChapterInfo[chapterCount];

        long currentChapterTicks = 0;
        for (int i = 0; i < chapterCount; i++)
        {
            chapters[i] = new ChapterInfo
            {
                StartPositionTicks = currentChapterTicks
            };

            currentChapterTicks += dummyChapterDuration;
        }

        return chapters;
    }

    /// <summary>
    /// Gets the chapters the server stores for a video: those of the Blu-ray structure or the probe result,
    /// or generated ones when there are fewer than two and generation is configured.
    /// </summary>
    /// <param name="probeChapters">The chapters of the probe result.</param>
    /// <param name="bluRayChapters">The chapter starts in seconds of a Blu-ray structure, if any.</param>
    /// <param name="runtimeTicks">The runtime of the video.</param>
    /// <param name="dummyChapterDurationSeconds">The configured duration of a generated chapter; 0 generates none.</param>
    /// <returns>The chapters.</returns>
    public static IReadOnlyList<ChapterInfo> GetVideoChapters(IReadOnlyList<ChapterInfo>? probeChapters, double[]? bluRayChapters, long runtimeTicks, int dummyChapterDurationSeconds)
    {
        IReadOnlyList<ChapterInfo> chapters = bluRayChapters is not null
            ? bluRayChapters.Select(c => new ChapterInfo { StartPositionTicks = TimeSpan.FromSeconds(c).Ticks }).ToArray()
            : probeChapters ?? [];

        if (dummyChapterDurationSeconds > 0 && chapters.Count <= 1)
        {
            try
            {
                chapters = CreateDummyChapters(runtimeTicks, dummyChapterDurationSeconds);
            }
            catch (ArgumentException)
            {
                chapters = [];
            }
        }

        return chapters;
    }

    /// <summary>
    /// Gets the chapters the server extracts images for.
    /// </summary>
    /// <param name="chapters">The chapters of the video.</param>
    /// <param name="runtimeTicks">The runtime of the video.</param>
    /// <returns>The chapters that get an image.</returns>
    public static IEnumerable<ChapterInfo> GetChaptersWithImages(IReadOnlyList<ChapterInfo> chapters, long runtimeTicks)
    {
        if (chapters.Count >= 2)
        {
            long sum = 0;
            for (int i = 1; i < chapters.Count; i++)
            {
                sum += chapters[i].StartPositionTicks - chapters[i - 1].StartPositionTicks;
            }

            if (sum / chapters.Count < TimeSpan.FromSeconds(1).Ticks)
            {
                return [];
            }
        }

        return chapters.TakeWhile(c => c.StartPositionTicks < runtimeTicks);
    }
}
