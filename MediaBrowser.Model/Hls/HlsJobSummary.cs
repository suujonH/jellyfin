using System;

namespace MediaBrowser.Model.Hls;

/// <summary>
/// A job of pre-encoded HLS, as the job list shows it.
/// </summary>
/// <param name="Id">The id of the job.</param>
/// <param name="Kind">What the job starts from.</param>
/// <param name="ItemId">The item the job was created for.</param>
/// <param name="Sha256">The hash of the content, once known.</param>
/// <param name="Status">The status.</param>
/// <param name="Attempt">How often the job was claimed.</param>
/// <param name="Worker">The worker that claimed the job last.</param>
/// <param name="Error">The last error.</param>
/// <param name="DateModified">When the job last changed, in UTC.</param>
public sealed record HlsJobSummary(Guid Id, HlsJobKind Kind, Guid? ItemId, string? Sha256, HlsJobStatus Status, int Attempt, string? Worker, string? Error, DateTime DateModified);
