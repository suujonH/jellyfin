using System;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// What recording a library file did.
/// </summary>
/// <param name="ItemId">The item.</param>
/// <param name="Change">What changed.</param>
/// <param name="PreviousSha256">The hash the file had: null when inserted, the kept hash when unchanged, the cleared one when its content changed.</param>
/// <param name="LinkedSha256">The content the file was linked to at once, as the file a worker added; null otherwise.</param>
public sealed record HlsUpsertResult(Guid ItemId, HlsSourceChange Change, string? PreviousSha256, string? LinkedSha256 = null);
