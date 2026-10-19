#pragma warning disable CS1591

using System.Collections.Generic;
using MediaBrowser.Model.MediaInfo;

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// The <c>[video]</c> section of a manifest; also used by disc folders.
/// </summary>
public sealed class TrueCacheVideoInfo
{
    /// <summary>Gets the transport stream timestamp kind; required for ts and m2ts containers.</summary>
    public TransportStreamTimestamp? Timestamp { get; init; }

    /// <summary>Gets the total runtime of all playable .vob files; required for DVD folders.</summary>
    public long? RuntimeTicks { get; init; }

    public IReadOnlyList<TrueCacheTrickplay> Trickplay { get; init; } = [];
}
