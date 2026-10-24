using System;
using System.Collections.Generic;
using MediaBrowser.Model.Hls;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A worker adding media that starts a job for some of its qualities, before the file is in the library.
/// </summary>
/// <param name="Worker">The name of the worker.</param>
/// <param name="WorkerId">The id of the worker.</param>
/// <param name="LibraryId">The library the media is added to.</param>
/// <param name="Sha256">The hash of the file, in its normal form.</param>
/// <param name="Probe">The probe of the file.</param>
/// <param name="Base">The root of the HLS store the worker uploads to, checked and in its normal form.</param>
/// <param name="Qualities">The video qualities to encode now, checked against those of the library.</param>
/// <param name="GetQualities">The video qualities of a library.</param>
/// <param name="Profile">The current encoding profile.</param>
/// <param name="Lease">How long the lease lasts without a heartbeat.</param>
/// <param name="Now">The current time.</param>
/// <param name="ResumeJobId">The job of an earlier addition of the media by this worker, to resume with its plan; <c>null</c> for none.</param>
/// <param name="ResumeToken">The lease token of that job.</param>
public sealed record HlsEagerJobRequest(
    string Worker,
    Guid WorkerId,
    Guid LibraryId,
    string Sha256,
    HlsProbe Probe,
    string Base,
    IReadOnlyCollection<int> Qualities,
    Func<Guid, IReadOnlyCollection<int>> GetQualities,
    string Profile,
    TimeSpan Lease,
    DateTime Now,
    Guid? ResumeJobId = null,
    string? ResumeToken = null);
