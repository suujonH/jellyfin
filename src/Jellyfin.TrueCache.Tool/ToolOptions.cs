using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using MediaBrowser.Controller.TrueCache;

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

        Options:
          --ffmpeg <path>              ffmpeg executable; ffprobe must be next to it (default: ffmpeg)
          --server-config <folder>     the server's configuration folder; its system.xml and encoding.xml
                                       decide the trickplay, chapter and encoding settings
          --trickplay-widths <w,...>   trickplay widths (default: the server's, 320)
          --trickplay-interval <ms>    trickplay interval in milliseconds (default: the server's, 10000)
          --trickplay-tile <WxH>       thumbnails per tile (default: the server's, 10x10)
          --dummy-chapters <seconds>   length of generated chapters for videos without chapters (default: the server's, 0)
          --kind <kind>                video, audio, dvd, bluray, book or photo instead of guessing from the path
          --force                      replace existing entries
        """;

    public string Command { get; private init; } = string.Empty;

    public IReadOnlyList<string> Paths { get; private init; } = [];

    public string FfmpegPath { get; private init; } = "ffmpeg";

    public string? ServerConfigDirectory { get; private init; }

    public int[]? TrickplayWidths { get; private init; }

    public int? TrickplayInterval { get; private init; }

    public (int Width, int Height)? TrickplayTile { get; private init; }

    public int? DummyChapterDuration { get; private init; }

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
        if (args.Length == 0 || args[0] is not ("generate" or "album" or "validate"))
        {
            throw new ArgumentException("A command is required.");
        }

        var paths = new List<string>();
        string ffmpeg = "ffmpeg";
        string? config = null;
        int[]? widths = null;
        int? interval = null;
        (int, int)? tile = null;
        int? dummy = null;
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

        return new ToolOptions
        {
            Command = args[0],
            Paths = paths,
            FfmpegPath = ffmpeg,
            ServerConfigDirectory = config,
            TrickplayWidths = widths,
            TrickplayInterval = interval,
            TrickplayTile = tile,
            DummyChapterDuration = dummy,
            Kind = kind,
            Force = force
        };
    }

    private static int ParsePositive(string value, string option)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0
            ? number
            : throw new ArgumentException(option + " needs positive numbers.");
}
