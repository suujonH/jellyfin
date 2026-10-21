using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using MediaBrowser.Model.Hls;

namespace Jellyfin.MediaEncoding.Hls.Published;

/// <summary>
/// Writes the master playlists of pre-encoded HLS from what is published. A master playlist is named <c>master</c> for every
/// quality or <c>{quality}p</c> for one, with <c>-a{index}</c> for one audio track; its URIs are relative to it:
/// <c>r/video/{quality}/index.m3u8</c>, <c>r/audio/{index}/index.m3u8</c> and <c>r/subtitles/{index}/index.m3u8</c>.
/// </summary>
public static partial class HlsMasterPlaylistWriter
{
    /// <summary>
    /// The name of the media playlist of a rendition.
    /// </summary>
    public const string MediaPlaylistFile = "index.m3u8";

    /// <summary>
    /// Gets the name of a master playlist, without its extension.
    /// </summary>
    /// <param name="media">What is published of the content.</param>
    /// <param name="quality">The video quality, or 0 for every quality.</param>
    /// <param name="audioIndex">The audio track, or <c>null</c> for every track; a track that is not published is left out.</param>
    /// <returns>The name, such as <c>master</c> or <c>1080p-a1</c>.</returns>
    public static string GetPlaylistName(HlsMediaView media, int quality, int? audioIndex)
    {
        ArgumentNullException.ThrowIfNull(media);
        var name = quality == 0 ? "master" : string.Create(CultureInfo.InvariantCulture, $"{quality}p");
        return audioIndex is { } audio && media.Find(HlsRenditionKind.Audio, audio) is not null
            ? string.Create(CultureInfo.InvariantCulture, $"{name}-a{audio}")
            : name;
    }

    /// <summary>
    /// Reads the name of a master playlist.
    /// </summary>
    /// <param name="name">The name, without its extension.</param>
    /// <param name="quality">The video quality, or 0 for every quality.</param>
    /// <param name="audioIndex">The audio track, or <c>null</c> for every track.</param>
    /// <returns>Whether the name is the name of a master playlist.</returns>
    public static bool TryParsePlaylistName(string? name, out int quality, out int? audioIndex)
    {
        quality = 0;
        audioIndex = null;
        var match = name is null ? Match.Empty : PlaylistName().Match(name);
        if (!match.Success)
        {
            return false;
        }

        if (match.Groups["q"].Success)
        {
            quality = int.Parse(match.Groups["q"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        if (match.Groups["a"].Success)
        {
            audioIndex = int.Parse(match.Groups["a"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture);
        }

        return true;
    }

    /// <summary>
    /// Gets the directory name a kind of rendition has in the URIs of a master playlist.
    /// </summary>
    /// <param name="kind">What the rendition holds.</param>
    /// <returns>The name, or <c>null</c> for a preview, which playlists do not list.</returns>
    public static string? GetKindName(HlsRenditionKind kind) => kind switch
    {
        HlsRenditionKind.Video => "video",
        HlsRenditionKind.Audio => "audio",
        HlsRenditionKind.Subtitle => "subtitles",
        _ => null
    };

    /// <summary>
    /// Reads the directory name a kind of rendition has in the URIs of a master playlist.
    /// </summary>
    /// <param name="name">The name.</param>
    /// <param name="kind">What the rendition holds.</param>
    /// <returns>Whether the name is that of a kind playlists list.</returns>
    public static bool TryParseKindName(string? name, out HlsRenditionKind kind)
    {
        foreach (var candidate in new[] { HlsRenditionKind.Video, HlsRenditionKind.Audio, HlsRenditionKind.Subtitle })
        {
            if (string.Equals(name, GetKindName(candidate), StringComparison.Ordinal))
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }

    /// <summary>
    /// Writes a master playlist. A quality or an audio track that is not published is left out, so the caller checks them first.
    /// </summary>
    /// <param name="media">What is published of the content.</param>
    /// <param name="quality">The video quality, or 0 for every quality.</param>
    /// <param name="audioIndex">The audio track, or <c>null</c> for every track.</param>
    /// <returns>The playlist, with line feeds.</returns>
    public static string Write(HlsMediaView media, int quality, int? audioIndex)
    {
        ArgumentNullException.ThrowIfNull(media);
        var variants = media.Video.Select(v => v.Summary).Where(s => quality == 0 || s.Quality == quality).ToArray();
        var audio = media.Audio.Select(a => a.Summary).Where(s => audioIndex is null || s.SourceIndex == audioIndex).ToArray();
        var subtitles = media.Subtitles.Select(s => s.Summary).ToArray();
        var defaultAudio = audioIndex ?? audio.FirstOrDefault(a => a.Default)?.SourceIndex ?? audio.FirstOrDefault()?.SourceIndex;

        var text = new StringBuilder("#EXTM3U\n#EXT-X-VERSION:7\n#EXT-X-INDEPENDENT-SEGMENTS\n");
        foreach (var track in audio)
        {
            var name = track.Title.Length == 0 ? string.Create(CultureInfo.InvariantCulture, $"Audio {track.SourceIndex}") : track.Title;
            text.Append(CultureInfo.InvariantCulture, $"#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",NAME=\"{Quote(name)} [{track.SourceIndex}]\",LANGUAGE=\"{Quote(Language(track.Language))}\",")
                .Append(CultureInfo.InvariantCulture, $"DEFAULT={(track.SourceIndex == defaultAudio ? "YES" : "NO")},AUTOSELECT=YES,CHANNELS=\"{track.Channels}\",")
                .Append(CultureInfo.InvariantCulture, $"URI=\"r/audio/{track.SourceIndex}/{MediaPlaylistFile}\"\n");
        }

        foreach (var subtitle in subtitles)
        {
            var name = subtitle.Title.Length == 0 ? string.Create(CultureInfo.InvariantCulture, $"Subtitles {subtitle.SourceIndex}") : subtitle.Title;
            text.Append(CultureInfo.InvariantCulture, $"#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"{Quote(name)} [{subtitle.SourceIndex}]\",LANGUAGE=\"{Quote(Language(subtitle.Language))}\",")
                .Append(CultureInfo.InvariantCulture, $"DEFAULT=NO,AUTOSELECT=YES,FORCED=NO,URI=\"r/subtitles/{subtitle.SourceIndex}/{MediaPlaylistFile}\"\n");
        }

        // The audio of a variant is any of the group, so its bit rates add the largest one.
        var audioRate = audio.Length == 0 ? 0 : audio.Max(a => a.Bandwidth);
        foreach (var variant in variants.OrderBy(v => v.Bandwidth))
        {
            text.Append(CultureInfo.InvariantCulture, $"#EXT-X-STREAM-INF:BANDWIDTH={variant.Bandwidth + audioRate},AVERAGE-BANDWIDTH={variant.AverageBandwidth + audioRate},")
                .Append(CultureInfo.InvariantCulture, $"RESOLUTION={variant.Width}x{variant.Height},FRAME-RATE={variant.FramesPerSecond.ToString("0.###", CultureInfo.InvariantCulture)},")
                .Append(CultureInfo.InvariantCulture, $"CODECS=\"{variant.Codec}{(audio.Length == 0 ? string.Empty : ",mp4a.40.2")}\"");
            if (audio.Length > 0)
            {
                text.Append(",AUDIO=\"audio\"");
            }

            if (subtitles.Length > 0)
            {
                text.Append(",SUBTITLES=\"subs\"");
            }

            text.Append(CultureInfo.InvariantCulture, $"\nr/video/{variant.Quality}/{MediaPlaylistFile}\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// Gets the language tag of a track: the common ISO 639-2 codes become two-letter tags, an empty one <c>und</c>.
    /// </summary>
    /// <param name="code">The language of the track.</param>
    /// <returns>The tag.</returns>
    public static string Language(string code) => code switch
    {
        "eng" => "en",
        "jpn" => "ja",
        "zho" or "chi" => "zh",
        "fra" or "fre" => "fr",
        "deu" or "ger" => "de",
        "spa" => "es",
        "kor" => "ko",
        "" => "und",
        _ => code
    };

    // A quoted attribute ends at a double quote or a line break, so neither may appear in it.
    private static string Quote(string value) => value.Replace('"', '\'').Replace('\r', ' ').Replace('\n', ' ');

    // ASCII digits only and "\z" instead of "$": "\d" would accept other scripts' digits and "$" a trailing line feed.
    [GeneratedRegex(@"^(?:master|(?<q>[0-9]{3,4})p)(?:-a(?<a>[0-9]{1,3}))?\z")]
    private static partial Regex PlaylistName();
}
