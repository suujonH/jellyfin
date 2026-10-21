using System;
using System.Linq;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// The settings of pre-encoded HLS as they are applied.
/// </summary>
/// <param name="Options">The settings; the defaults, which leave pre-encoded HLS off, while the saved ones are invalid.</param>
/// <param name="Store">The HLS store, or <c>null</c> when none is set.</param>
/// <param name="Error">What is wrong with the saved settings, or <c>null</c>.</param>
/// <param name="Version">Counts the saves of the settings.</param>
public sealed record HlsSettingsSnapshot(HlsOptions Options, IHlsStore? Store, string? Error, int Version)
{
    /// <summary>
    /// Gets how long a worker holds a job without a heartbeat.
    /// </summary>
    public TimeSpan Lease => TimeSpan.FromMinutes(Options.LeaseMinutes);

    /// <summary>
    /// Gets whether the files of a library are pre-encoded.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <returns>Whether pre-encoded HLS is on and the library is chosen.</returns>
    public bool IsLibraryEligible(Guid libraryId)
        => Options.Enabled && (Options.Libraries.Length == 0 || Options.Libraries.Contains(libraryId));
}
