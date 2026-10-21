namespace MediaBrowser.Controller.TrueCache;

/// <summary>
/// The validation result of a <c>__true_cache__</c> entry, as reported by the API.
/// </summary>
/// <param name="ResourcePath">The resource.</param>
/// <param name="State">The state of the entry.</param>
/// <param name="Reason">Why the entry was rejected; <c>null</c> otherwise.</param>
public sealed record TrueCacheValidationResult(string ResourcePath, TrueCacheState State, string? Reason);
