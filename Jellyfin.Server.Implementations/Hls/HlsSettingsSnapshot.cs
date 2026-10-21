using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// The settings of pre-encoded HLS as they are applied.
/// </summary>
/// <param name="Options">The settings; the defaults, which leave pre-encoded HLS off, while the saved ones are invalid.</param>
/// <param name="Error">What is wrong with the saved settings, or <c>null</c>.</param>
/// <param name="Version">Counts the saves of the settings.</param>
public sealed record HlsSettingsSnapshot(HlsOptions Options, string? Error, int Version)
{
    /// <summary>
    /// Gets how long a worker holds a job without a heartbeat, which is also how long the directories of a lease that ended are
    /// kept for the uploads still under way.
    /// </summary>
    public TimeSpan Lease => TimeSpan.FromMinutes(Options.LeaseMinutes);

    /// <summary>
    /// Gets whether the files of a library are pre-encoded.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <returns>Whether pre-encoded HLS is on and the library is chosen.</returns>
    public bool IsLibraryEligible(Guid libraryId)
        => Options.Enabled && (Options.Libraries.Length == 0 || Options.Libraries.Contains(libraryId));

    /// <summary>
    /// Gets the video qualities the files of a library are encoded in: the global ones, narrowed by those of the library when it
    /// has its own.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <returns>The qualities; none when the library is not pre-encoded.</returns>
    public IReadOnlyCollection<int> GetQualities(Guid libraryId)
    {
        if (!IsLibraryEligible(libraryId))
        {
            return [];
        }

        var own = Options.LibraryQualities.FirstOrDefault(l => l.LibraryId.Equals(libraryId));
        return own is null ? Options.Qualities : Options.Qualities.Where(own.Qualities.Contains).ToArray();
    }
}
