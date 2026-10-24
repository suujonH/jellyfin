using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// An HLS worker the server registered; media the worker keeps on its own machine is named by its id.
/// </summary>
public class HlsWorkerIdentity
{
    /// <summary>
    /// Gets or sets the id, given by the server or chosen by the worker.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets the name the worker last gave.
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Gets or sets when the worker was registered.
    /// </summary>
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// Gets or sets when the worker last registered.
    /// </summary>
    public DateTime DateLastSeen { get; set; }
}
