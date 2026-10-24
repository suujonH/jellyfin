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
/// <param name="Base">The root of the HLS store the worker uploads to, checked and in its normal form.</param>
/// <param name="GetQualities">The video qualities of a library.</param>
/// <param name="Profile">The current encoding profile.</param>
/// <param name="IsLibraryEligible">Whether pre-encoded HLS is enabled for a library.</param>
/// <param name="ExcludedQualities">The video qualities the worker does not encode, which are left for another job.</param>
/// <param name="Now">The current time.</param>
public sealed record HlsIdentifyRequest(
    Guid JobId,
    string LeaseToken,
    string Sha256,
    HlsProbe Probe,
    string Base,
    Func<Guid, IReadOnlyCollection<int>> GetQualities,
    string Profile,
    Func<Guid, bool> IsLibraryEligible,
    IReadOnlySet<int> ExcludedQualities,
    DateTime Now);
