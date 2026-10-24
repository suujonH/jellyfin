using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// Which renditions a worker that excludes some video qualities takes.
/// </summary>
public static class HlsQualityRules
{
    /// <summary>
    /// Tells whether a rendition is a video quality, <c>v{height}</c>.
    /// </summary>
    /// <param name="key">The key of the rendition.</param>
    /// <returns>Whether it is a video quality.</returns>
    public static bool IsVideoKey(string key)
        => key.Length > 1 && key[0] == 'v' && key.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>
    /// Tells whether a rendition is a video quality the worker excludes.
    /// </summary>
    /// <param name="key">The key of the rendition.</param>
    /// <param name="excluded">The heights the worker excludes.</param>
    /// <returns>Whether the worker does not encode it.</returns>
    public static bool IsExcluded(string key, IReadOnlySet<int> excluded)
        => excluded.Count > 0
            && IsVideoKey(key)
            && int.TryParse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var quality)
            && excluded.Contains(quality);

    /// <summary>
    /// Tells whether a worker takes a job: it holds no video quality, or one the worker does not exclude. The audio, subtitles and
    /// preview go with the video.
    /// </summary>
    /// <param name="keys">The renditions of the job.</param>
    /// <param name="excluded">The heights the worker excludes.</param>
    /// <returns>Whether the worker takes the job.</returns>
    public static bool CanTake(IEnumerable<string> keys, IReadOnlySet<int> excluded)
    {
        if (excluded.Count == 0)
        {
            return true;
        }

        var video = keys.Where(IsVideoKey).ToList();
        return video.Count == 0 || video.Any(k => !IsExcluded(k, excluded));
    }
}
