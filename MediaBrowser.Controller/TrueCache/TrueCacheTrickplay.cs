#pragma warning disable CS1591

using System.Globalization;

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// One trickplay resolution of a video, exactly as the server stores it.
/// </summary>
public sealed class TrueCacheTrickplay
{
    public int Width { get; init; }

    /// <summary>Gets the height of one thumbnail.</summary>
    public int Height { get; init; }

    public int TileWidth { get; init; }

    public int TileHeight { get; init; }

    public int ThumbnailCount { get; init; }

    /// <summary>Gets the interval between thumbnails in milliseconds.</summary>
    public int Interval { get; init; }

    /// <summary>Gets the peak bandwidth of the tiles in bits per second.</summary>
    public int Bandwidth { get; init; }

    /// <summary>Gets the number of tile images.</summary>
    public int TileCount => (ThumbnailCount + (TileWidth * TileHeight) - 1) / (TileWidth * TileHeight);

    /// <summary>Gets the folder of the tiles, relative to the trickplay folder.</summary>
    public string FolderName => string.Create(CultureInfo.InvariantCulture, $"{Width} - {TileWidth}x{TileHeight}");
}
