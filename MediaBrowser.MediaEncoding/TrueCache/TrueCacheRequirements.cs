using System.Collections.Generic;

namespace MediaBrowser.MediaEncoding.TrueCache;

/// <summary>
/// The server settings that decide what a complete <c>__true_cache__</c> entry must hold.
/// </summary>
/// <param name="TrickplayWidths">The configured trickplay widths; an entry needs tiles for each.</param>
/// <param name="TrickplayInterval">The configured trickplay interval in milliseconds; shorter videos get no trickplay.</param>
/// <param name="DummyChapterDuration">The configured duration in seconds of generated chapters; 0 generates none.</param>
public sealed record TrueCacheRequirements(IReadOnlyList<int> TrickplayWidths, int TrickplayInterval, int DummyChapterDuration);
