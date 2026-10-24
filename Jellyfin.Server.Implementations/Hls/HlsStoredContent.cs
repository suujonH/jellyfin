using System;
using System.Collections.Generic;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A content with what is published of it and the items whose file it is.
/// </summary>
/// <param name="Sha256">The hash.</param>
/// <param name="LibraryId">The library the content was first found in.</param>
/// <param name="DateOrphaned">When no library file was left of it, or null.</param>
/// <param name="Renditions">The published renditions, by key.</param>
/// <param name="ItemIds">The items whose file is the content, in order.</param>
public sealed record HlsStoredContent(string Sha256, Guid LibraryId, DateTime? DateOrphaned, IReadOnlyList<HlsStoredRendition> Renditions, IReadOnlyList<Guid> ItemIds);
