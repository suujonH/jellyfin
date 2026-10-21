using System;
using System.Collections.Generic;
using MediaBrowser.Model.Hls;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A worker reporting what the file of its job is.
/// </summary>
/// <param name="JobId">The job.</param>
/// <param name="LeaseToken">The token of the lease.</param>
/// <param name="Sha256">The hash of the file.</param>
/// <param name="Probe">The probe of the file.</param>
/// <param name="Qualities">The qualities chosen in the settings.</param>
/// <param name="Profile">The current encoding profile.</param>
/// <param name="IsLibraryEligible">Whether pre-encoded HLS is enabled for a library.</param>
/// <param name="Now">The current time.</param>
public sealed record HlsIdentifyRequest(
    Guid JobId,
    string LeaseToken,
    string Sha256,
    HlsProbe Probe,
    IReadOnlyCollection<int> Qualities,
    string Profile,
    Func<Guid, bool> IsLibraryEligible,
    DateTime Now);
