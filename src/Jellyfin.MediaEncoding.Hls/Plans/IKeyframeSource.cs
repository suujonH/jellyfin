using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Keyframes;
using MediaBrowser.Model.MediaInfo;

namespace Jellyfin.MediaEncoding.Hls.Plans;

/// <summary>
/// Finds the keyframes a keyframe plan is made from.
/// </summary>
public interface IKeyframeSource
{
    /// <summary>
    /// Gets the keyframes of the first video stream of a video, from its <c>__true_cache__</c> entry, the database, the container
    /// index or, for a local file, ffprobe within the configured time.
    /// </summary>
    /// <param name="itemId">The id of the video, which is the media source id.</param>
    /// <param name="mediaPath">The path or address the video is read from.</param>
    /// <param name="protocol">The protocol of <paramref name="mediaPath"/>.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The keyframes, or null when there are none in time; the video is then transcoded.</returns>
    Task<KeyframeData?> GetKeyframesAsync(Guid itemId, string mediaPath, MediaProtocol protocol, CancellationToken cancellationToken);
}
