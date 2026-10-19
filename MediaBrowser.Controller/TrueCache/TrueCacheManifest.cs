#pragma warning disable CS1591

using System;

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// The declaration of a <c>__true_cache__</c> entry, read from <c>meta.toml</c>.
/// </summary>
public sealed class TrueCacheManifest
{
    /// <summary>The only format version this server understands.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; init; }

    public TrueCacheKind Kind { get; init; }

    /// <summary>Gets the declared size of the resource in bytes; required for files.</summary>
    public long? SourceSize { get; init; }

    /// <summary>Gets the declared last write time of the resource; checked only when declared.</summary>
    public DateTime? SourceModified { get; init; }

    public TrueCacheVideoInfo? Video { get; init; }

    public TrueCacheAudioInfo? Audio { get; init; }

    public TrueCacheBookInfo? Book { get; init; }

    public TrueCachePhotoInfo? Photo { get; init; }

    /// <summary>Gets the integrated loudness of a whole album; only for <see cref="TrueCacheKind.Album"/>.</summary>
    public float? AlbumLufs { get; init; }
}
