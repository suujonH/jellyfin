using System.Collections.Generic;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// What publishing the outputs of a job did.
/// </summary>
/// <param name="Sha256">The hash of the content.</param>
/// <param name="PublishedKeys">The renditions that were published; the outputs of the others are queued for deletion.</param>
public sealed record HlsPublishResult(string Sha256, IReadOnlyList<string> PublishedKeys);
