using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using MediaBrowser.Model.Hls;

namespace Jellyfin.MediaEncoding.Hls.Published;

/// <summary>
/// Reads what a worker reports about a file.
/// </summary>
public static class HlsProbeReader
{
    /// <summary>
    /// Reads the output of <c>ffprobe -show_streams -show_format -of json</c>.
    /// </summary>
    /// <param name="json">The output.</param>
    /// <returns>The probe.</returns>
    /// <exception cref="ArgumentException">The file has no video, or no valid size, duration or frame rate.</exception>
    /// <exception cref="JsonException">The output is not JSON.</exception>
    /// <exception cref="InvalidOperationException">The output is JSON of another shape.</exception>
    /// <exception cref="System.Collections.Generic.KeyNotFoundException">The output has no streams or no format.</exception>
    public static HlsProbe Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var streams = root.GetProperty("streams").EnumerateArray().ToArray();

        // Cover art is a video stream as well.
        var video = streams.FirstOrDefault(s => Text(s, "codec_type") == "video" && !Flag(s, "attached_pic"));
        if (video.ValueKind == JsonValueKind.Undefined)
        {
            throw new ArgumentException("The file has no video stream.", nameof(json));
        }

        var duration = Number(root.GetProperty("format"), "duration");
        var width = Integer(video, "width");
        var height = Integer(video, "height");
        var fps = Rational(Text(video, "avg_frame_rate"));
        if (fps <= 0)
        {
            fps = Rational(Text(video, "r_frame_rate"));
        }

        if (width <= 0 || height <= 0 || duration <= 0 || fps <= 0 || !double.IsFinite(duration) || !double.IsFinite(fps))
        {
            throw new ArgumentException("The size, duration and frame rate of the video must be positive numbers.", nameof(json));
        }

        var rotation = 0;
        if (video.TryGetProperty("side_data_list", out var sideData) && sideData.ValueKind == JsonValueKind.Array)
        {
            foreach (var side in sideData.EnumerateArray())
            {
                if (side.TryGetProperty("rotation", out var value) && value.TryGetDouble(out var degrees))
                {
                    rotation = (int)degrees;
                }
            }
        }

        var (sarNumerator, sarDenominator) = AspectRatio(Text(video, "sample_aspect_ratio"));
        return new HlsProbe(
            width,
            height,
            fps,
            duration,
            Text(video, "codec_name"),
            Text(video, "pix_fmt"),
            Text(video, "color_transfer"),
            Text(video, "field_order"),
            rotation,
            streams.Where(s => Text(s, "codec_type") == "audio")
                .Select(s => new HlsAudioTrack(Integer(s, "index"), Text(s, "codec_name"), Integer(s, "channels"), Integer(s, "sample_rate"), Tag(s, "language"), Tag(s, "title"), Flag(s, "default")))
                .ToArray(),
            streams.Where(s => Text(s, "codec_type") == "subtitle")
                .Select(s => new HlsSubtitleTrack(Integer(s, "index"), Text(s, "codec_name"), Tag(s, "language"), Tag(s, "title"), Flag(s, "default")))
                .ToArray(),
            Integer(video, "index"),
            sarNumerator,
            sarDenominator,
            Text(video, "color_space"));
    }

    /// <summary>
    /// Checks a SHA-256 hash and writes it in lower case.
    /// </summary>
    /// <param name="sha256">The hash, as 64 hexadecimal characters.</param>
    /// <returns>The hash in lower case.</returns>
    /// <exception cref="ArgumentException">The hash is not 64 hexadecimal characters.</exception>
    public static string NormalizeSha256(string sha256)
    {
        if (sha256 is not { Length: 64 } || !sha256.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("A SHA-256 hash must be 64 hexadecimal characters.", nameof(sha256));
        }

        return sha256.ToLowerInvariant();
    }

    private static string Text(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value.ToString() : string.Empty;

    private static int Integer(JsonElement element, string name)
        => int.TryParse(Text(element, name), CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static double Number(JsonElement element, string name)
        => double.TryParse(Text(element, name), CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string Tag(JsonElement element, string name)
        => element.TryGetProperty("tags", out var tags) ? Text(tags, name) : string.Empty;

    private static bool Flag(JsonElement element, string name)
        => element.TryGetProperty("disposition", out var flags) && Integer(flags, name) == 1;

    // ffprobe prints "a:b"; "0:1" and "N/A" mean unknown, so everything that is not two positive integers counts as square pixels.
    private static (int Numerator, int Denominator) AspectRatio(string text)
    {
        var parts = text.Split(':');
        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var numerator) && numerator > 0
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var denominator) && denominator > 0
                ? (numerator, denominator)
                : (1, 1);
    }

    private static double Rational(string text)
    {
        var parts = text.Split('/');
        if (!double.TryParse(parts[0], CultureInfo.InvariantCulture, out var numerator))
        {
            return 0;
        }

        if (parts.Length == 2 && double.TryParse(parts[1], CultureInfo.InvariantCulture, out var denominator))
        {
            return denominator == 0 ? 0 : numerator / denominator;
        }

        return numerator;
    }
}
