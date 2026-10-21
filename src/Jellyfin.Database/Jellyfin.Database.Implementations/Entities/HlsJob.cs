using System;

namespace Jellyfin.Database.Implementations.Entities;

/// <summary>
/// A job of pre-encoded HLS: a worker identifies a file and encodes the renditions its content lacks.
/// </summary>
public class HlsJob
{
    /// <summary>
    /// Gets or sets the id; a job created again gets a new one.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Gets or sets what the job starts from, a <c>MediaBrowser.Model.Hls.HlsJobKind</c>.
    /// </summary>
    public int Kind { get; set; }

    /// <summary>
    /// Gets or sets the work the job does: <c>source:{item}</c> or <c>media:{sha256}</c>; there is one job per work.
    /// </summary>
    public required string WorkKey { get; set; }

    /// <summary>
    /// Gets or sets the item the job was created for.
    /// </summary>
    public Guid? ItemId { get; set; }

    /// <summary>
    /// Gets or sets the hash of the content, once known.
    /// </summary>
    public string? Sha256 { get; set; }

    /// <summary>
    /// Gets or sets the root of the HLS store the worker uploads the plan to, as it reported it when it identified the file.
    /// </summary>
    public string? Base { get; set; }

    /// <summary>
    /// Gets or sets the file the worker reads, a <c>MediaBrowser.Model.Hls.HlsSourceFile</c> as JSON, chosen when the job is claimed.
    /// </summary>
    public string? Input { get; set; }

    /// <summary>
    /// Gets or sets what the worker encodes, a <c>MediaBrowser.Model.Hls.HlsEncodePlan</c> as JSON, made when the file is identified.
    /// </summary>
    public string? Plan { get; set; }

    /// <summary>
    /// Gets or sets the status, a <c>MediaBrowser.Model.Hls.HlsJobStatus</c>.
    /// </summary>
    public int Status { get; set; }

    /// <summary>
    /// Gets or sets the name of the worker that claimed the job last.
    /// </summary>
    public string? Worker { get; set; }

    /// <summary>
    /// Gets or sets the token of the lease, which also names the directories of the attempt.
    /// </summary>
    public string? LeaseToken { get; set; }

    /// <summary>
    /// Gets or sets when the lease expires.
    /// </summary>
    public DateTime? LeaseExpires { get; set; }

    /// <summary>
    /// Gets or sets how often the job was claimed.
    /// </summary>
    public int Attempt { get; set; }

    /// <summary>
    /// Gets or sets the last error.
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Gets or sets when the job was created; the oldest pending job is claimed first.
    /// </summary>
    public DateTime DateCreated { get; set; }

    /// <summary>
    /// Gets or sets when the job last changed.
    /// </summary>
    public DateTime DateModified { get; set; }
}
