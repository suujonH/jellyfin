using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Hls;

namespace Jellyfin.MediaEncoding.Hls.Published;

/// <summary>
/// The layout of pre-encoded HLS in the HLS store: <c>{media}/video/{quality}/{attempt}</c>, <c>{media}/audio/{index}/{attempt}</c>,
/// <c>{media}/subtitles/{index}/{attempt}</c> and <c>{media}/preview/{attempt}</c>, the layout of the DistributedHls plugin.
/// The attempt is the lease token of the job, so a worker never writes into a directory that is published.
/// </summary>
public static partial class HlsRenditionPaths
{
    /// <summary>
    /// The init segment of a video or audio rendition.
    /// </summary>
    public const string InitFile = "init.mp4";

    /// <summary>
    /// The file of a subtitle rendition.
    /// </summary>
    public const string SubtitleFile = "subtitle.vtt";

    /// <summary>
    /// The file of a preview rendition.
    /// </summary>
    public const string PreviewFile = "preview.jpg";

    private const string SegmentPrefix = "segment-";
    private const string SegmentExtension = ".m4s";

    /// <summary>
    /// Gets the directory name of a content: the first 32 hexadecimal characters of its hash, in lower case.
    /// </summary>
    /// <param name="sha256">The hash of the content.</param>
    /// <returns>The directory name.</returns>
    public static string GetMediaKey(string sha256) => HlsProbeReader.NormalizeSha256(sha256)[..32];

    /// <summary>
    /// Gets the directory of a rendition.
    /// </summary>
    /// <param name="mediaKey">The directory name of the content.</param>
    /// <param name="spec">The rendition.</param>
    /// <param name="attempt">The lease token of the job.</param>
    /// <returns>The directory, relative to the root of the HLS store.</returns>
    public static string GetRelativeDirectory(string mediaKey, HlsRenditionSpec spec, string attempt)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return spec.Kind switch
        {
            HlsRenditionKind.Video => string.Create(
                CultureInfo.InvariantCulture,
                $"{mediaKey}/video/{(spec.Video ?? throw new ArgumentException("A video rendition needs its quality.", nameof(spec))).Name}/{attempt}"),
            HlsRenditionKind.Audio => string.Create(CultureInfo.InvariantCulture, $"{mediaKey}/audio/{spec.SourceIndex}/{attempt}"),
            HlsRenditionKind.Subtitle => string.Create(CultureInfo.InvariantCulture, $"{mediaKey}/subtitles/{spec.SourceIndex}/{attempt}"),
            HlsRenditionKind.Preview => $"{mediaKey}/preview/{attempt}",
            _ => throw new ArgumentOutOfRangeException(nameof(spec))
        };
    }

    /// <summary>
    /// Checks a directory before it is stored or deleted: only directories of the layout are ever touched.
    /// </summary>
    /// <param name="relativeDirectory">The directory.</param>
    /// <returns>Whether the directory is a rendition directory of the layout.</returns>
    public static bool IsValidRelativeDirectory(string? relativeDirectory)
        => relativeDirectory is not null && ValidDirectory().IsMatch(relativeDirectory);

    /// <summary>
    /// Gets the name of a segment file.
    /// </summary>
    /// <param name="index">The index of the segment, from 0.</param>
    /// <returns>The name, such as <c>segment-000000.m4s</c>.</returns>
    public static string GetSegmentFile(int index) => string.Create(CultureInfo.InvariantCulture, $"{SegmentPrefix}{index:D6}{SegmentExtension}");

    /// <summary>
    /// Reads the index of a segment file.
    /// </summary>
    /// <param name="name">The name of the file.</param>
    /// <param name="index">The index of the segment.</param>
    /// <returns>Whether the name is exactly the name <see cref="GetSegmentFile"/> gives.</returns>
    public static bool TryGetSegmentIndex(string? name, out int index)
    {
        index = -1;
        if (name is null
            || name.Length != SegmentPrefix.Length + 6 + SegmentExtension.Length
            || !name.StartsWith(SegmentPrefix, StringComparison.Ordinal)
            || !name.EndsWith(SegmentExtension, StringComparison.Ordinal))
        {
            return false;
        }

        var digits = name.AsSpan(SegmentPrefix.Length, 6);
        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        index = int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// Gets whether a worker may upload a file into a rendition directory.
    /// </summary>
    /// <param name="kind">What the rendition holds.</param>
    /// <param name="name">The name of the file.</param>
    /// <param name="expectedSegments">How many segments the plan expects; one more is allowed.</param>
    /// <returns>Whether the file belongs to a rendition of the kind.</returns>
    public static bool IsAllowedFile(HlsRenditionKind kind, string? name, int expectedSegments) => kind switch
    {
        HlsRenditionKind.Video or HlsRenditionKind.Audio => string.Equals(name, InitFile, StringComparison.Ordinal)
            || (TryGetSegmentIndex(name, out var index) && index <= expectedSegments),
        HlsRenditionKind.Subtitle => string.Equals(name, SubtitleFile, StringComparison.Ordinal),
        HlsRenditionKind.Preview => string.Equals(name, PreviewFile, StringComparison.Ordinal),
        _ => false
    };

    /// <summary>
    /// Gets the files a rendition must have besides its segments.
    /// </summary>
    /// <param name="kind">What the rendition holds.</param>
    /// <returns>The names of the files.</returns>
    public static IReadOnlyList<string> GetRequiredFiles(HlsRenditionKind kind) => kind switch
    {
        HlsRenditionKind.Video or HlsRenditionKind.Audio => [InitFile],
        HlsRenditionKind.Subtitle => [SubtitleFile],
        HlsRenditionKind.Preview => [PreviewFile],
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    // ASCII digits only and "\z" instead of "$": "\d" would accept other scripts' digits and "$" a trailing line feed.
    [GeneratedRegex(@"^[0-9a-f]{32}/(video/[0-9]{3,4}|audio/[0-9]{1,3}|subtitles/[0-9]{1,3}|preview)/[0-9a-f]{32}\z")]
    private static partial Regex ValidDirectory();
}
