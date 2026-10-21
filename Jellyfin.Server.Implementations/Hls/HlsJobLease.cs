using System;
using MediaBrowser.Model.Hls;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A job a worker holds the lease of.
/// </summary>
/// <param name="Id">The job.</param>
/// <param name="Kind">What the job starts from.</param>
/// <param name="Input">The file the worker reads.</param>
/// <param name="LeaseToken">The token of the lease.</param>
/// <param name="LeaseExpires">When the lease expires.</param>
/// <param name="Attempt">How often the job was claimed, this time included.</param>
/// <param name="Plan">What the worker encodes, once the file is identified.</param>
public sealed record HlsJobLease(Guid Id, HlsJobKind Kind, HlsSourceFile Input, string LeaseToken, DateTime LeaseExpires, int Attempt, HlsEncodePlan? Plan);
