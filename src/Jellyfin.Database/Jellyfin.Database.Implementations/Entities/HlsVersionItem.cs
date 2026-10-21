using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// The item that stands for a published quality of an item, as an alternate version of it.
/// </summary>
public class HlsVersionItem
{
    /// <summary>
    /// Gets or sets the original item.
    /// </summary>
    public Guid ItemId { get; set; }

    /// <summary>
    /// Gets or sets the quality; 0 means automatic.
    /// </summary>
    public int Quality { get; set; }

    /// <summary>
    /// Gets or sets the id of the version item.
    /// </summary>
    public Guid VersionId { get; set; }

    /// <summary>
    /// Gets or sets what the version item was last written from, to skip writes that change nothing.
    /// </summary>
    public required string Signature { get; set; }
}
