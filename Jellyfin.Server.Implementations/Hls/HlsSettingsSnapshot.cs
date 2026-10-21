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
    /// Gets how long an address a worker uploads to is valid.
    /// </summary>
    public static TimeSpan UploadLifetime { get; } = TimeSpan.FromHours(1);

    /// <summary>
    /// Gets how long a directory a worker may still upload to is kept after the lease ended: until the addresses it was given
    /// expired, and a lease more for the uploads under way then.
    /// </summary>
    public TimeSpan UploadGrace => UploadLifetime + Lease;

    /// <summary>
    /// Gets whether the files of a library are pre-encoded.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <returns>Whether pre-encoded HLS is on and the library is chosen.</returns>
    public bool IsLibraryEligible(Guid libraryId)
        => Options.Enabled && (Options.Libraries.Length == 0 || Options.Libraries.Contains(libraryId));
}
