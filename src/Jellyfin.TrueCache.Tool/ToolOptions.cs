using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Entities;

namespace Jellyfin.TrueCache.Tool;

/// <summary>
/// The command line of the generator.
/// </summary>
internal sealed class ToolOptions
{
    public const string Usage = """
        Generates __true_cache__ entries with the server's own probing and extraction code.
        Run it while the media files are still on a local disk, before they are uploaded.

        Usage:
          Jellyfin.TrueCache.Tool generate [options] <media file or disc folder>...
          Jellyfin.TrueCache.Tool album [options] <album folder>...
          Jellyfin.TrueCache.Tool validate [options] <media file or disc folder>...
          Jellyfin.TrueCache.Tool strm [options] --name <name>.strm --output <folder> <local media file>

        The strm command writes the entry of a .strm that will name the media file once it is uploaded: the entry is
        written to <folder>/<name>.strm, to be put in the __true_cache__ folder next to the .strm. Its source.size is the
        size of the local file and it has no source.modified.

        Options:
          --ffmpeg <path>              ffmpeg executable; ffprobe must be next to it (default: ffmpeg)
          --server-config <folder>     the server's configuration folder; its system.xml and encoding.xml
                                       decide the trickplay, chapter and encoding settings
          --server-data <folder>       the server's data folder; the network storage accesses of its jellyfin.db
                                       are used to read s3:// targets of .strm files
          --server-url <url>           the server, whose GET /System/Configuration and the encoding configuration
                                       decide the settings instead of --server-config; needs --api-key
          --api-key <key>              an API key of the server
          --name <name>.strm           strm: the file name of the .strm
          --output <folder>            strm: the folder the entry is written to
          --trickplay-widths <w,...>   trickplay widths (default: the server's, 320)
          --trickplay-interval <ms>    trickplay interval in milliseconds (default: the server's, 10000)
          --trickplay-tile <WxH>       thumbnails per tile (default: the server's, 10x10)
          --dummy-chapters <seconds>   length of generated chapters for videos without chapters (default: the server's, 0)
          --hwaccel <type>             none, nvenc, qsv, amf or vaapi: the hardware trickplay decodes with, instead of the
                                       server's hardware acceleration (default: the server's settings)
          --hw-device <device>         with --hwaccel nvenc, the GPU index; with --hwaccel vaapi, the render node,
                                       such as /dev/dri/renderD128; with --hwaccel qsv, the adapter index on Windows
                                       or the render node on Linux (default: the first GPU)
          --kind <kind>                video, audio, dvd, bluray, book or photo instead of guessing from the path;
                                       video or audio for strm
          --force                      replace existing entries
        """;

    public string Command { get; private init; } = string.Empty;

    public IReadOnlyList<string> Paths { get; private init; } = [];

    public string FfmpegPath { get; private init; } = "ffmpeg";

    public string? ServerConfigDirectory { get; private init; }

    public string? ServerDataDirectory { get; private init; }

    public Uri? ServerUrl { get; private init; }

    public string? ApiKey { get; private init; }

    public string? EntryName { get; private init; }

    public string? OutputDirectory { get; private init; }

    public int[]? TrickplayWidths { get; private init; }

    public int? TrickplayInterval { get; private init; }

    public (int Width, int Height)? TrickplayTile { get; private init; }

    public int? DummyChapterDuration { get; private init; }

    public HardwareAccelerationType? HardwareAcceleration { get; private init; }

    public string? HardwareDevice { get; private init; }

    public TrueCacheKind? Kind { get; private init; }

    public bool Force { get; private init; }

    /// <summary>
    /// Parses the command line.
    /// </summary>
    /// <param name="args">The arguments.</param>
    /// <returns>The options.</returns>
    /// <exception cref="ArgumentException">The command line is invalid.</exception>
    public static ToolOptions Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("generate" or "album" or "validate" or "strm"))
        {
            throw new ArgumentException("A command is required.");
        }

        var paths = new List<string>();
        string ffmpeg = "ffmpeg";
        string? config = null;
        string? data = null;
        Uri? url = null;
        string? apiKey = null;
        string? name = null;
        string? output = null;
        int[]? widths = null;
        int? interval = null;
        (int, int)? tile = null;
        int? dummy = null;
        HardwareAccelerationType? hwaccel = null;
        string? device = null;
        TrueCacheKind? kind = null;
        var force = false;

        for (var i = 1; i < args.Length; i++)
        {
            string Next() => ++i < args.Length ? args[i] : throw new ArgumentException(args[i - 1] + " needs a value.");

            switch (args[i])
            {
                case "--ffmpeg":
                    ffmpeg = Next();
                    break;
                case "--server-config":
                    config = Next();
                    break;
                case "--server-data":
                    data = Next();
                    break;
                case "--server-url":
                    url = Uri.TryCreate(Next(), UriKind.Absolute, out var parsed) && (parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps)
                        ? parsed
                        : throw new ArgumentException("--server-url needs an http or https address.");
                    break;
                case "--api-key":
                    apiKey = Next();
                    break;
                case "--name":
                    name = Next();
                    break;
                case "--output":
                    output = Next();
                    break;
                case "--trickplay-widths":
                    widths = Next().Split(',').Select(w => ParsePositive(w, "--trickplay-widths")).ToArray();
                    break;
                case "--trickplay-interval":
                    interval = ParsePositive(Next(), "--trickplay-interval");
                    break;
                case "--trickplay-tile":
                    var parts = Next().Split('x');
                    tile = parts.Length == 2
                        ? (ParsePositive(parts[0], "--trickplay-tile"), ParsePositive(parts[1], "--trickplay-tile"))
                        : throw new ArgumentException("--trickplay-tile must look like 10x10.");
                    break;
                case "--dummy-chapters":
                    dummy = int.Parse(Next(), NumberStyles.None, CultureInfo.InvariantCulture);
                    break;
                case "--kind":
                    var value = Next();
                    kind = value switch
                    {
                        "video" => TrueCacheKind.Video,
                        "audio" => TrueCacheKind.Audio,
                        "dvd" => TrueCacheKind.Dvd,
                        "bluray" => TrueCacheKind.BluRay,
                        "book" => TrueCacheKind.Book,
                        "photo" => TrueCacheKind.Photo,
                        _ => throw new ArgumentException("Unknown kind " + value + ".")
                    };
                    break;
                case "--hwaccel":
                    var type = Next();
                    hwaccel = type switch
                    {
                        "none" => HardwareAccelerationType.none,
                        "nvenc" => HardwareAccelerationType.nvenc,
                        "qsv" => HardwareAccelerationType.qsv,
                        "amf" => HardwareAccelerationType.amf,
                        "vaapi" => HardwareAccelerationType.vaapi,
                        _ => throw new ArgumentException("Unknown hardware acceleration " + type + ".")
                    };
                    break;
                case "--hw-device":
                    device = Next();
                    break;
                case "--force":
                    force = true;
                    break;
                default:
                    if (args[i].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new ArgumentException("Unknown option " + args[i] + ".");
                    }

                    paths.Add(args[i]);
                    break;
            }
        }

        if (paths.Count == 0)
        {
            throw new ArgumentException("At least one path is required.");
        }

        if (url is not null && config is not null)
        {
            throw new ArgumentException("--server-url and --server-config are exclusive.");
        }

        if ((url is null) != (apiKey is null))
        {
            throw new ArgumentException("--server-url and --api-key go together.");
        }

        if (device is not null && hwaccel is not (HardwareAccelerationType.nvenc or HardwareAccelerationType.vaapi or HardwareAccelerationType.qsv))
        {
            throw new ArgumentException("--hw-device goes with --hwaccel nvenc, vaapi or qsv.");
        }

        if (hwaccel == HardwareAccelerationType.nvenc && device is not null && !int.TryParse(device, NumberStyles.None, CultureInfo.InvariantCulture, out _))
        {
            throw new ArgumentException("--hw-device of nvenc is a GPU index.");
        }

        if (args[0] == "strm")
        {
            if (paths.Count != 1)
            {
                throw new ArgumentException("strm takes one media file.");
            }

            // The name becomes a folder of the output: a file name, as the .strm next to its __true_cache__ folder has.
            if (name is null
                || !TrueCacheLayout.IsShortcut(name)
                || name.Length == ".strm".Length
                || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0
                || name.IndexOfAny(['/', '\\']) >= 0
                || name.StartsWith('.'))
            {
                throw new ArgumentException("strm needs --name with the file name of the .strm.");
            }

            if (output is null)
            {
                throw new ArgumentException("strm needs --output.");
            }

            if (kind is not (null or TrueCacheKind.Video or TrueCacheKind.Audio))
            {
                throw new ArgumentException("A .strm names a video or an audio file.");
            }
        }
        else if (name is not null || output is not null)
        {
            throw new ArgumentException("--name and --output are options of strm.");
        }

        return new ToolOptions
        {
            Command = args[0],
            Paths = paths,
            FfmpegPath = ffmpeg,
            ServerConfigDirectory = config,
            ServerDataDirectory = data,
            ServerUrl = url,
            ApiKey = apiKey,
            EntryName = name,
            OutputDirectory = output,
            TrickplayWidths = widths,
            TrickplayInterval = interval,
            TrickplayTile = tile,
            DummyChapterDuration = dummy,
            HardwareAcceleration = hwaccel,
            HardwareDevice = device,
            Kind = kind,
            Force = force
        };
    }

    private static int ParsePositive(string value, string option)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : throw new ArgumentException(option + " needs positive numbers.");
}
