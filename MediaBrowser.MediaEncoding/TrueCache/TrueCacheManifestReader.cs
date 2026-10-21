using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.MediaInfo;
using Tomlyn;
using Tomlyn.Model;

namespace MediaBrowser.MediaEncoding.TrueCache;

/// <summary>
/// Reads <c>meta.toml</c>. Unknown keys are refused, so a misspelt key cannot silently leave a value undeclared.
/// </summary>
public static class TrueCacheManifestReader
{
    private static readonly string[] _rootKeys = ["version", "kind", "source", "video", "audio", "book", "photo", "album"];
    private static readonly string[] _sourceKeys = ["size", "modified"];
    private static readonly string[] _videoKeys = ["timestamp", "runtime_ticks", "trickplay"];
    private static readonly string[] _trickplayKeys = ["width", "height", "tile_width", "tile_height", "thumbnail_count", "interval", "bandwidth"];
    private static readonly string[] _audioKeys = ["lufs", "embedded_lyrics", "tags"];
    private static readonly string[] _tagKeys =
        ["title", "album", "artists", "album_artists", "composers", "genres", "year", "track", "disc", "date", "description", "comment", "publisher", "id3v1_only", "additional"];

    private static readonly string[] _bookKeys = ["page_count", "comic_info", "comic_book_info", "opf", "cover"];
    private static readonly string[] _photoKeys =
    [
        "width", "height", "camera_make", "camera_model", "rating", "comment", "title", "date_taken", "genres", "keywords", "software",
        "orientation", "exposure_time", "focal_length", "latitude", "longitude", "altitude", "iso_speed", "aperture", "shutter_speed"
    ];

    private static readonly string[] _albumKeys = ["lufs"];

    /// <summary>
    /// Parses a declaration.
    /// </summary>
    /// <param name="text">The content of <c>meta.toml</c>.</param>
    /// <returns>The declaration.</returns>
    /// <exception cref="FormatException">The declaration is not valid.</exception>
    public static TrueCacheManifest Read(string text)
    {
        TomlTable root;
        try
        {
            root = Toml.ToModel(text);
        }
        catch (TomlException ex)
        {
            throw new FormatException("meta.toml is not valid TOML: " + ex.Message, ex);
        }

        CheckKeys(root, _rootKeys, string.Empty);
        var version = GetInteger(root, "version", string.Empty) ?? throw new FormatException("version is required");
        if (version != TrueCacheManifest.CurrentVersion)
        {
            throw new FormatException($"version {version} is not supported");
        }

        var kindText = GetString(root, "kind", string.Empty) ?? throw new FormatException("kind is required");
        var kind = kindText switch
        {
            "video" => TrueCacheKind.Video,
            "audio" => TrueCacheKind.Audio,
            "dvd" => TrueCacheKind.Dvd,
            "bluray" => TrueCacheKind.BluRay,
            "book" => TrueCacheKind.Book,
            "photo" => TrueCacheKind.Photo,
            "album" => TrueCacheKind.Album,
            _ => throw new FormatException($"kind '{kindText}' is not supported")
        };

        var source = GetTable(root, "source", string.Empty);
        if (source is not null)
        {
            CheckKeys(source, _sourceKeys, "source.");
        }

        return new TrueCacheManifest
        {
            Version = (int)version,
            Kind = kind,
            SourceSize = source is null ? null : GetInteger(source, "size", "source."),
            SourceModified = source is null ? null : GetDate(source, "modified", "source."),
            Video = ReadVideo(GetTable(root, "video", string.Empty)),
            Audio = ReadAudio(GetTable(root, "audio", string.Empty)),
            Book = ReadBook(GetTable(root, "book", string.Empty)),
            Photo = ReadPhoto(GetTable(root, "photo", string.Empty)),
            AlbumLufs = ReadAlbum(GetTable(root, "album", string.Empty))
        };
    }

    private static TrueCacheVideoInfo? ReadVideo(TomlTable? table)
    {
        if (table is null)
        {
            return null;
        }

        const string Prefix = "video.";
        CheckKeys(table, _videoKeys, Prefix);
        var timestamp = GetString(table, "timestamp", Prefix);
        var trickplay = new List<TrueCacheTrickplay>();
        if (table.TryGetValue("trickplay", out var value))
        {
            if (value is not TomlTableArray array)
            {
                throw new FormatException("video.trickplay must be an array of tables");
            }

            foreach (var item in array)
            {
                const string ItemPrefix = "video.trickplay.";
                CheckKeys(item, _trickplayKeys, ItemPrefix);
                trickplay.Add(new TrueCacheTrickplay
                {
                    Width = GetRequiredInt(item, "width", ItemPrefix),
                    Height = GetRequiredInt(item, "height", ItemPrefix),
                    TileWidth = GetRequiredInt(item, "tile_width", ItemPrefix),
                    TileHeight = GetRequiredInt(item, "tile_height", ItemPrefix),
                    ThumbnailCount = GetRequiredInt(item, "thumbnail_count", ItemPrefix),
                    Interval = GetRequiredInt(item, "interval", ItemPrefix),
                    Bandwidth = GetRequiredInt(item, "bandwidth", ItemPrefix)
                });
            }
        }

        return new TrueCacheVideoInfo
        {
            Timestamp = timestamp switch
            {
                null => null,
                "none" => TransportStreamTimestamp.None,
                "zero" => TransportStreamTimestamp.Zero,
                "valid" => TransportStreamTimestamp.Valid,
                _ => throw new FormatException($"video.timestamp '{timestamp}' must be none, zero or valid")
            },
            RuntimeTicks = GetInteger(table, "runtime_ticks", Prefix),
            Trickplay = trickplay
        };
    }

    private static TrueCacheAudioInfo? ReadAudio(TomlTable? table)
    {
        if (table is null)
        {
            return null;
        }

        const string Prefix = "audio.";
        CheckKeys(table, _audioKeys, Prefix);
        var tags = GetTable(table, "tags", Prefix);
        return new TrueCacheAudioInfo
        {
            Lufs = (float?)GetNumber(table, "lufs", Prefix),
            EmbeddedLyrics = GetBool(table, "embedded_lyrics", Prefix),
            Tags = tags is null ? null : ReadTags(tags)
        };
    }

    private static TrueCacheAudioTags ReadTags(TomlTable table)
    {
        const string Prefix = "audio.tags.";
        CheckKeys(table, _tagKeys, Prefix);
        var additional = new Dictionary<string, string>(StringComparer.Ordinal);
        var additionalTable = GetTable(table, "additional", Prefix);
        if (additionalTable is not null)
        {
            foreach (var (key, value) in additionalTable)
            {
                additional[key] = value switch
                {
                    string text => text,
                    TomlArray array => string.Join(TrueCacheAudioTags.ValueSeparator, array.Select(v => v as string ?? throw new FormatException($"{Prefix}additional.{key} must hold strings"))),
                    _ => throw new FormatException($"{Prefix}additional.{key} must be a string or an array of strings")
                };
            }
        }

        return new TrueCacheAudioTags
        {
            Title = GetString(table, "title", Prefix),
            Album = GetString(table, "album", Prefix),
            Artists = GetStrings(table, "artists", Prefix),
            AlbumArtists = GetStrings(table, "album_artists", Prefix),
            Composers = GetStrings(table, "composers", Prefix),
            Genres = GetStrings(table, "genres", Prefix),
            Year = (int?)GetInteger(table, "year", Prefix),
            TrackNumber = (int?)GetInteger(table, "track", Prefix),
            DiscNumber = (int?)GetInteger(table, "disc", Prefix),
            Date = GetDate(table, "date", Prefix),
            Description = GetString(table, "description", Prefix),
            Comment = GetString(table, "comment", Prefix),
            Publisher = GetString(table, "publisher", Prefix),
            Id3v1Only = GetBool(table, "id3v1_only", Prefix) ?? false,
            AdditionalFields = additional
        };
    }

    private static TrueCacheBookInfo? ReadBook(TomlTable? table)
    {
        if (table is null)
        {
            return null;
        }

        const string Prefix = "book.";
        CheckKeys(table, _bookKeys, Prefix);
        return new TrueCacheBookInfo
        {
            PageCount = GetInteger(table, "page_count", Prefix),
            ComicInfo = GetBool(table, "comic_info", Prefix),
            ComicBookInfo = GetBool(table, "comic_book_info", Prefix),
            Opf = GetBool(table, "opf", Prefix),
            Cover = GetBool(table, "cover", Prefix)
        };
    }

    private static TrueCachePhotoInfo? ReadPhoto(TomlTable? table)
    {
        if (table is null)
        {
            return null;
        }

        const string Prefix = "photo.";
        CheckKeys(table, _photoKeys, Prefix);
        var orientation = GetString(table, "orientation", Prefix);
        return new TrueCachePhotoInfo
        {
            Width = GetRequiredInt(table, "width", Prefix),
            Height = GetRequiredInt(table, "height", Prefix),
            CameraMake = GetString(table, "camera_make", Prefix),
            CameraModel = GetString(table, "camera_model", Prefix),
            Rating = GetNumber(table, "rating", Prefix),
            Comment = GetString(table, "comment", Prefix),
            Title = GetString(table, "title", Prefix),
            DateTaken = GetDate(table, "date_taken", Prefix),
            Genres = GetStrings(table, "genres", Prefix),
            Keywords = GetStrings(table, "keywords", Prefix),
            Software = GetString(table, "software", Prefix),
            Orientation = orientation is null
                ? null
                : Enum.TryParse<ImageOrientation>(orientation, true, out var parsed) && Enum.IsDefined(parsed)
                    ? parsed
                    : throw new FormatException($"photo.orientation '{orientation}' is not an image orientation"),
            ExposureTime = GetNumber(table, "exposure_time", Prefix),
            FocalLength = GetNumber(table, "focal_length", Prefix),
            Latitude = GetNumber(table, "latitude", Prefix),
            Longitude = GetNumber(table, "longitude", Prefix),
            Altitude = GetNumber(table, "altitude", Prefix),
            IsoSpeedRating = (int?)GetInteger(table, "iso_speed", Prefix),
            Aperture = GetNumber(table, "aperture", Prefix),
            ShutterSpeed = GetNumber(table, "shutter_speed", Prefix)
        };
    }

    private static float? ReadAlbum(TomlTable? table)
    {
        if (table is null)
        {
            return null;
        }

        CheckKeys(table, _albumKeys, "album.");
        return (float?)GetNumber(table, "lufs", "album.");
    }

    private static void CheckKeys(TomlTable table, string[] allowed, string prefix)
    {
        foreach (var key in table.Keys)
        {
            if (!allowed.Contains(key, StringComparer.Ordinal))
            {
                throw new FormatException($"unknown key {prefix}{key}");
            }
        }
    }

    private static TomlTable? GetTable(TomlTable table, string key, string prefix) => table.TryGetValue(key, out var value)
        ? value as TomlTable ?? throw new FormatException($"{prefix}{key} must be a table")
        : null;

    private static string? GetString(TomlTable table, string key, string prefix) => table.TryGetValue(key, out var value)
        ? value as string ?? throw new FormatException($"{prefix}{key} must be a string")
        : null;

    private static IReadOnlyList<string> GetStrings(TomlTable table, string key, string prefix)
    {
        if (!table.TryGetValue(key, out var value))
        {
            return [];
        }

        return value switch
        {
            string text => [text],
            TomlArray array => array.Select(v => v as string ?? throw new FormatException($"{prefix}{key} must hold strings")).ToArray(),
            _ => throw new FormatException($"{prefix}{key} must be a string or an array of strings")
        };
    }

    private static long? GetInteger(TomlTable table, string key, string prefix) => table.TryGetValue(key, out var value)
        ? value as long? ?? throw new FormatException($"{prefix}{key} must be an integer")
        : null;

    private static int GetRequiredInt(TomlTable table, string key, string prefix)
    {
        var value = GetInteger(table, key, prefix) ?? throw new FormatException($"{prefix}{key} is required");
        if (value is < 0 or > int.MaxValue)
        {
            throw new FormatException($"{prefix}{key} is out of range");
        }

        return (int)value;
    }

    private static double? GetNumber(TomlTable table, string key, string prefix) => table.TryGetValue(key, out var value)
        ? value switch
        {
            double number => number,
            long number => number,
            _ => throw new FormatException($"{prefix}{key} must be a number")
        }
        : null;

    private static bool? GetBool(TomlTable table, string key, string prefix) => table.TryGetValue(key, out var value)
        ? value as bool? ?? throw new FormatException($"{prefix}{key} must be true or false")
        : null;

    private static DateTime? GetDate(TomlTable table, string key, string prefix)
    {
        if (!table.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            TomlDateTime date => date.Kind is TomlDateTimeKind.OffsetDateTimeByZ or TomlDateTimeKind.OffsetDateTimeByNumber
                ? date.DateTime.UtcDateTime
                : DateTime.SpecifyKind(date.DateTime.DateTime, DateTimeKind.Unspecified),
            string text when DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) => parsed,
            _ => throw new FormatException($"{prefix}{key} must be a date")
        };
    }
}
