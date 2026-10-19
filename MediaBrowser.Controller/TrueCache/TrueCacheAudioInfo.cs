#pragma warning disable CS1591

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// The <c>[audio]</c> section of a manifest.
/// </summary>
public sealed class TrueCacheAudioInfo
{
    /// <summary>Gets the integrated loudness of the track.</summary>
    public float? Lufs { get; init; }

    /// <summary>Gets whether the file has embedded lyrics; when it has, <c>lyrics.lrc</c> holds them.</summary>
    public bool? EmbeddedLyrics { get; init; }

    public TrueCacheAudioTags? Tags { get; init; }
}
