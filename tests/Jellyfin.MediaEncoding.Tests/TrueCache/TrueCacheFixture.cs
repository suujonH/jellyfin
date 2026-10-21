using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.MediaEncoding.TrueCache;

namespace Jellyfin.MediaEncoding.Tests.TrueCache
{
    /// <summary>
    /// A temporary folder with a media file and its <c>__true_cache__</c> entry.
    /// </summary>
    public sealed class TrueCacheFixture : IDisposable
    {
        /// <summary>A probe result with a video, an audio, an ASS subtitle stream, a font attachment and two chapters, 100 seconds long.</summary>
        public const string VideoProbe = """
            {
              "streams": [
                { "index": 0, "codec_name": "h264", "codec_type": "video", "width": 1920, "height": 1080, "pix_fmt": "yuv420p", "r_frame_rate": "24/1", "avg_frame_rate": "24/1", "disposition": { "default": 1 } },
                { "index": 1, "codec_name": "aac", "codec_type": "audio", "channels": 2, "sample_rate": "48000", "disposition": { "default": 1 }, "tags": { "language": "eng" } },
                { "index": 2, "codec_name": "ass", "codec_type": "subtitle", "disposition": { "default": 0 }, "tags": { "language": "jpn", "title": "Signs" } },
                { "index": 3, "codec_name": "ttf", "codec_type": "attachment", "tags": { "filename": "font.ttf", "mimetype": "application/x-truetype-font" } }
              ],
              "chapters": [
                { "id": 0, "start_time": "0.000000", "end_time": "50.000000", "tags": { "title": "Opening" } },
                { "id": 1, "start_time": "50.000000", "end_time": "100.000000", "tags": { "title": "Ending" } }
              ],
              "format": { "filename": "Film.mkv", "format_name": "matroska,webm", "duration": "100.000000", "size": "4", "bit_rate": "8000000" }
            }
            """;

        /// <summary>A probe result of an audio file with an embedded cover, 200 seconds long.</summary>
        public const string AudioProbe = """
            {
              "streams": [
                { "index": 0, "codec_name": "mp3", "codec_type": "audio", "channels": 2, "sample_rate": "44100", "bit_rate": "320000" },
                { "index": 1, "codec_name": "mjpeg", "codec_type": "video", "width": 500, "height": 500, "disposition": { "attached_pic": 1 }, "tags": { "comment": "Cover (front)" } }
              ],
              "format": { "filename": "Song.mp3", "format_name": "mp3", "duration": "200.000000", "size": "4", "bit_rate": "320000" }
            }
            """;

        public const string VideoMeta = """
            version = 1
            kind = "video"

            [source]
            size = 4

            [[video.trickplay]]
            width = 320
            height = 180
            tile_width = 10
            tile_height = 10
            thumbnail_count = 10
            interval = 10000
            bandwidth = 12345
            """;

        public const string AudioMeta = """
            version = 1
            kind = "audio"

            [source]
            size = 4

            [audio]
            lufs = -14.5
            embedded_lyrics = true

            [audio.tags]
            title = "Song"
            album = "Album"
            artists = ["A", "B"]
            album_artists = ["A"]
            genres = ["Rock"]
            year = 2001
            track = 3
            disc = 1

            [audio.tags.additional]
            REPLAYGAIN_TRACK_GAIN = "-6.5 dB"
            """;

        public TrueCacheFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "jellyfin-truecache-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Root);
        }

        public static TrueCacheRequirements Requirements { get; } = new([320], 10000, 0);

        public string Root { get; }

        /// <summary>
        /// Creates a media file of four bytes.
        /// </summary>
        public string CreateResource(string name)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            return path;
        }

        /// <summary>
        /// Writes files into the entry of a resource.
        /// </summary>
        public string WriteEntry(string resourcePath, IReadOnlyDictionary<string, string> files)
        {
            var directory = TrueCacheLayout.GetEntryDirectory(resourcePath)!;
            foreach (var (name, content) in files)
            {
                var path = Path.Combine(directory, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, content);
            }

            return directory;
        }

        /// <summary>
        /// Creates a video and a complete entry for it.
        /// </summary>
        public string CreateCompleteVideo(string name = "Film.mkv")
        {
            var path = CreateResource(name);
            WriteEntry(path, CompleteVideoFiles());
            return path;
        }

        public static Dictionary<string, string> CompleteVideoFiles() => new()
        {
            [TrueCacheLayout.MetaFile] = VideoMeta,
            [TrueCacheLayout.ProbeFile] = VideoProbe,
            [TrueCacheLayout.ScreenGrabFile] = "jpg",
            ["chapters/0.jpg"] = "jpg",
            ["chapters/500000000.jpg"] = "jpg",
            ["trickplay/320 - 10x10/0.jpg"] = "jpg",
            ["subtitles/2.ass"] = "[Script Info]",
            ["attachments/font.ttf"] = "ttf",
            [TrueCacheLayout.KeyframesFile] = """{ "TotalDuration": 1000000000, "KeyframeTicks": [0, 20000000, 40000000] }"""
        };

        public static Dictionary<string, string> CompleteAudioFiles() => new()
        {
            [TrueCacheLayout.MetaFile] = AudioMeta,
            [TrueCacheLayout.ProbeFile] = AudioProbe,
            [TrueCacheLayout.LyricsFile] = "[00:01.00]Hello",
            ["images/primary.jpg"] = "jpg"
        };

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
