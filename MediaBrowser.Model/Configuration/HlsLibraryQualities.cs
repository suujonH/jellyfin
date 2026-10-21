#pragma warning disable CA1819 // XML serialization handles collections improperly, so we need to use arrays

using System;

namespace MediaBrowser.Model.Configuration;

/// <summary>
/// The video qualities of one library, which narrow the global ones.
/// </summary>
public class HlsLibraryQualities
{
    /// <summary>
    /// Gets or sets the library.
    /// </summary>
    public Guid LibraryId { get; set; }

    /// <summary>
    /// Gets or sets the video qualities, by the height of the picture; only those also chosen globally are encoded.
    /// </summary>
    public int[] Qualities { get; set; } = [];
}
