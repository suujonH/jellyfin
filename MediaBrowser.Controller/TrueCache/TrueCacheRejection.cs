using System;

namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// A resource that was rejected because its <c>__true_cache__</c> entry is invalid or incomplete.
/// </summary>
/// <param name="ResourcePath">The resource.</param>
/// <param name="Reason">Why the entry was rejected.</param>
/// <param name="CheckedAt">When the entry was validated.</param>
public sealed record TrueCacheRejection(string ResourcePath, string Reason, DateTime CheckedAt);
