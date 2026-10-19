#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using MediaBrowser.Model.Drawing;

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// The <c>[photo]</c> section of a manifest: the image tags the photo provider reads.
/// </summary>
public sealed class TrueCachePhotoInfo
{
    public int Width { get; init; }

    public int Height { get; init; }

    public string? CameraMake { get; init; }

    public string? CameraModel { get; init; }

    public double? Rating { get; init; }

    public string? Comment { get; init; }

    public string? Title { get; init; }

    public DateTime? DateTaken { get; init; }

    public IReadOnlyList<string> Genres { get; init; } = [];

    public IReadOnlyList<string> Keywords { get; init; } = [];

    public string? Software { get; init; }

    public ImageOrientation? Orientation { get; init; }

    public double? ExposureTime { get; init; }

    public double? FocalLength { get; init; }

    public double? Latitude { get; init; }

    public double? Longitude { get; init; }

    public double? Altitude { get; init; }

    public int? IsoSpeedRating { get; init; }

    public double? Aperture { get; init; }

    public double? ShutterSpeed { get; init; }
}
