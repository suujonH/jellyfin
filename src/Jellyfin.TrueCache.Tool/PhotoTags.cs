using System;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Drawing;
using Microsoft.Extensions.Logging;
using TagLib;
using TagLib.IFD;
using TagLib.IFD.Entries;
using TagLib.IFD.Tags;
using Tomlyn;
using Tomlyn.Model;

namespace Jellyfin.TrueCache.Tool;

/// <summary>
/// Reads the image tags of a photo the way the photo provider does.
/// </summary>
internal static class PhotoTags
{
    // Other extensions might cause taglib to hang; the photo provider uses the same list.
    private static readonly string[] _includeExtensions = [".jpg", ".jpeg", ".png", ".tiff", ".cr2", ".webp", ".avif"];

    public static TomlTable Read(string path, IImageEncoder imageEncoder, ILogger logger)
    {
        var table = new TomlTable();
        var width = 0;
        var height = 0;

        void Set(string key, object? value)
        {
            if (value is not null)
            {
                table[key] = value;
            }
        }

        if (_includeExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var file = TagLib.File.Create(path);
                if (file.GetTag(TagTypes.TiffIFD) is IFDTag tag
                    && tag.Structure?.GetEntry(0, (ushort)IFDEntryTag.ExifIFD) is SubIFDEntry exif
                    && exif.Structure is { } exifStructure)
                {
                    if (exifStructure.GetEntry(0, (ushort)ExifEntryTag.ApertureValue) is RationalIFDEntry aperture)
                    {
                        table["aperture"] = (double)aperture.Value.Numerator / aperture.Value.Denominator;
                    }

                    if (exifStructure.GetEntry(0, (ushort)ExifEntryTag.ShutterSpeedValue) is RationalIFDEntry shutterSpeed)
                    {
                        table["shutter_speed"] = (double)shutterSpeed.Value.Numerator / shutterSpeed.Value.Denominator;
                    }
                }

                if (file is TagLib.Image.File image)
                {
                    var imageTag = image.ImageTag;
                    width = image.Properties.PhotoWidth;
                    height = image.Properties.PhotoHeight;
                    Set("camera_make", imageTag.Make);
                    Set("camera_model", imageTag.Model);
                    Set("rating", imageTag.Rating is { } rating ? (double)rating : null);
                    Set("comment", imageTag.Comment);
                    Set("title", imageTag.Title);
                    if (imageTag.DateTime is { } dateTaken)
                    {
                        table["date_taken"] = new TomlDateTime(new DateTimeOffset(dateTaken.ToUniversalTime()), 0, TomlDateTimeKind.OffsetDateTimeByZ);
                    }

                    table["genres"] = ToArray(imageTag.Genres);
                    table["keywords"] = ToArray(imageTag.Keywords);
                    Set("software", imageTag.Software);
                    if (imageTag.Orientation != TagLib.Image.ImageOrientation.None)
                    {
                        table["orientation"] = imageTag.Orientation.ToString();
                    }

                    Set("exposure_time", imageTag.ExposureTime);
                    Set("focal_length", imageTag.FocalLength);
                    Set("latitude", imageTag.Latitude);
                    Set("longitude", imageTag.Longitude);
                    Set("altitude", imageTag.Altitude);
                    Set("iso_speed", imageTag.ISOSpeedRatings is { } iso ? (long)iso : null);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error reading image tags of {Path}", path);
            }
        }

        // Without tags, the server measures the image.
        if (width <= 0 || height <= 0)
        {
            var size = imageEncoder.GetImageSize(path);
            width = size.Width;
            height = size.Height;
        }

        table["width"] = (long)width;
        table["height"] = (long)height;
        return table;
    }

    private static TomlArray ToArray(string[]? values)
    {
        var array = new TomlArray();
        foreach (var value in values ?? [])
        {
            array.Add(value);
        }

        return array;
    }
}
