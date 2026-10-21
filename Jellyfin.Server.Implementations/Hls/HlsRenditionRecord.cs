using System;
using MediaBrowser.Model.Hls;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A rendition of a content.
/// </summary>
/// <param name="Sha256">The hash of the content.</param>
/// <param name="Key">The key.</param>
/// <param name="State">The state.</param>
/// <param name="JobId">The job that holds the rendition.</param>
/// <param name="PublishedJobId">The job whose output is published.</param>
/// <param name="Base">The root of the HLS store the published directory is in.</param>
/// <param name="RelativeDirectory">The published directory.</param>
/// <param name="Summary">The summary of the published output.</param>
/// <param name="Profile">The encoding profile it was published under.</param>
public sealed record HlsRenditionRecord(
    string Sha256,
    string Key,
    HlsRenditionState State,
    Guid? JobId,
    Guid? PublishedJobId,
    string? Base,
    string? RelativeDirectory,
    HlsRenditionSummary? Summary,
    string? Profile);
