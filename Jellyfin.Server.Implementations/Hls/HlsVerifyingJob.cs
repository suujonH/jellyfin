using System;
using System.Collections.Generic;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A job whose outputs wait to be verified.
/// </summary>
/// <param name="Id">The job.</param>
/// <param name="Sha256">The hash of the content.</param>
/// <param name="Attempt">How often the job was claimed.</param>
/// <param name="Outputs">The reported renditions.</param>
public sealed record HlsVerifyingJob(Guid Id, string Sha256, int Attempt, IReadOnlyList<HlsVerifyingOutput> Outputs);
