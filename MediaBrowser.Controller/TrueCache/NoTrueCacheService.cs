using System;
using System.Collections.Generic;

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// An <see cref="ITrueCacheService"/> that finds no entries, for callers that do not use <c>__true_cache__</c>.
/// </summary>
public sealed class NoTrueCacheService : ITrueCacheService
{
    private NoTrueCacheService()
    {
    }

    /// <summary>Gets the instance.</summary>
    public static NoTrueCacheService Instance { get; } = new();

    /// <inheritdoc />
    public TrueCacheLookup Lookup(string resourcePath) => TrueCacheLookup.None;

    /// <inheritdoc />
    public TrueCacheLookup Validate(string resourcePath) => TrueCacheLookup.None;

    /// <inheritdoc />
    public TrueCacheLookup LookupAlbum(string albumPath) => TrueCacheLookup.None;

    /// <inheritdoc />
    public bool HasDeclarationChanged(string resourcePath, DateTime since) => false;

    /// <inheritdoc />
    public IReadOnlyList<TrueCacheRejection> GetRejections() => [];
}
