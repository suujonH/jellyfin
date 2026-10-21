using System;
using System.IO;

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// Names of the files and folders of a <c>__true_cache__</c> entry.
/// </summary>
/// <remarks>
/// A resource <c>Dir/Film.mkv</c> (or a disc folder <c>Dir/Film</c>) is described by <c>Dir/__true_cache__/Film.mkv/</c>.
/// Everything in that folder is declared by the user and trusted as is.
/// </remarks>
public static class TrueCacheLayout
{
    /// <summary>The name of the folder that sits next to the resources it describes.</summary>
    public const string FolderName = "__true_cache__";

    /// <summary>The declaration of an entry.</summary>
    public const string MetaFile = "meta.toml";

    /// <summary>The raw ffprobe output, produced with the arguments the server uses.</summary>
    public const string ProbeFile = "probe.json";

    /// <summary>The screen grab of a video.</summary>
    public const string ScreenGrabFile = "primary.jpg";

    /// <summary>The folder with embedded images, named after their image type (<c>primary.png</c>, <c>backdrop.jpg</c>, ...).</summary>
    public const string ImagesFolder = "images";

    /// <summary>The folder with chapter images, named <c>{StartPositionTicks}.jpg</c>.</summary>
    public const string ChaptersFolder = "chapters";

    /// <summary>The folder with trickplay tiles, laid out as <c>{width} - {tileWidth}x{tileHeight}/{n}.jpg</c>.</summary>
    public const string TrickplayFolder = "trickplay";

    /// <summary>The folder with extracted embedded subtitles, named <c>{streamIndex}.{extension}</c>.</summary>
    public const string SubtitlesFolder = "subtitles";

    /// <summary>The folder with extracted attachments, named like the server's attachment cache.</summary>
    public const string AttachmentsFolder = "attachments";

    /// <summary>The keyframes of a video.</summary>
    public const string KeyframesFile = "keyframes.json";

    /// <summary>The embedded lyrics of an audio file.</summary>
    public const string LyricsFile = "lyrics.lrc";

    /// <summary>The Blu-ray structure of a disc folder.</summary>
    public const string BdInfoFile = "bdinfo.json";

    /// <summary>The folder with documents and the cover of a book.</summary>
    public const string BookFolder = "book";

    /// <summary>The ComicInfo document of a comic archive.</summary>
    public const string ComicInfoFile = "ComicInfo.xml";

    /// <summary>The ComicBookInfo archive comment of a comic archive.</summary>
    public const string ComicBookInfoFile = "comicbookinfo.json";

    /// <summary>The OPF package document of an EPUB.</summary>
    public const string OpfFile = "content.opf";

    /// <summary>The base name of a book cover; the extension gives the image format.</summary>
    public const string CoverName = "cover";

    /// <summary>The entry of an album folder, holding album-wide values.</summary>
    public const string AlbumEntry = "__album__";

    /// <summary>
    /// Gets whether a file system entry is a cache folder.
    /// </summary>
    /// <param name="name">The name of the entry.</param>
    /// <returns><c>true</c> for a cache folder.</returns>
    public static bool IsCacheFolderName(ReadOnlySpan<char> name)
        => name.Equals(FolderName, StringComparison.Ordinal);

    /// <summary>
    /// Gets whether a resource is a .strm, whose declared size is the size of the media it names.
    /// </summary>
    /// <param name="resourcePath">The path of the resource.</param>
    /// <returns><c>true</c> for a .strm.</returns>
    public static bool IsShortcut(string resourcePath)
        => resourcePath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Gets the entry folder that describes a resource.
    /// </summary>
    /// <param name="resourcePath">The absolute path of the media file or disc folder.</param>
    /// <returns>The entry folder, or <c>null</c> when the path has no parent.</returns>
    public static string? GetEntryDirectory(string resourcePath)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(resourcePath);
        var parent = Path.GetDirectoryName(trimmed);
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(name) || IsCacheFolderName(name))
        {
            return null;
        }

        return Path.Combine(parent, FolderName, name);
    }

    /// <summary>
    /// Gets the entry that holds album-wide values of a folder.
    /// </summary>
    /// <param name="albumPath">The album folder.</param>
    /// <returns>The entry folder.</returns>
    public static string GetAlbumEntryDirectory(string albumPath)
        => Path.Combine(Path.TrimEndingDirectorySeparator(albumPath), FolderName, AlbumEntry);

    /// <summary>
    /// Gets the resource described by a path inside a cache folder.
    /// </summary>
    /// <param name="path">A path that may lie inside a cache folder.</param>
    /// <param name="resourcePath">The resource path, when <paramref name="path"/> lies in an entry folder.</param>
    /// <returns><c>true</c> when the path lies inside a cache folder.</returns>
    public static bool TryGetResourceOfCachePath(string path, out string? resourcePath)
    {
        resourcePath = null;
        var current = Path.TrimEndingDirectorySeparator(path);
        string? child = null;
        while (!string.IsNullOrEmpty(current))
        {
            var name = Path.GetFileName(current);
            if (IsCacheFolderName(name))
            {
                var parent = Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(parent) && !string.IsNullOrEmpty(child) && !string.Equals(child, AlbumEntry, StringComparison.Ordinal))
                {
                    resourcePath = Path.Combine(parent, child);
                }

                return true;
            }

            child = name;
            current = Path.GetDirectoryName(current);
        }

        return false;
    }
}
