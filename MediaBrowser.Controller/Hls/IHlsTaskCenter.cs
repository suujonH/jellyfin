using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Hls;

namespace MediaBrowser.Controller.Hls;

/// <summary>
/// The task center of pre-encoded HLS, as the worker API and the management API use it. A request that does not make sense
/// throws <see cref="ArgumentException"/>; storage that cannot be reached throws <see cref="IOException"/>.
/// </summary>
public interface IHlsTaskCenter
{
    /// <summary>
    /// Claims the oldest job a worker can do.
    /// </summary>
    /// <param name="worker">The name of the worker.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The job, or <c>null</c> when there is none or pre-encoded HLS is off.</returns>
    Task<HlsClaimedJob?> PollAsync(string worker, CancellationToken cancellationToken);

    /// <summary>
    /// Gets where the worker reads the file of its job.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="leaseToken">The token of the lease.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The input, or <c>null</c> when the lease is not valid.</returns>
    Task<HlsJobInput?> GetInputAsync(Guid jobId, string leaseToken, CancellationToken cancellationToken);

    /// <summary>
    /// Extends a lease.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="leaseToken">The token of the lease.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the lease was valid.</returns>
    Task<bool> HeartbeatAsync(Guid jobId, string leaseToken, CancellationToken cancellationToken);

    /// <summary>
    /// Records what the file of a job is and decides what the job encodes.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="identification">What the worker found, and the root of the HLS store it uploads to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The plan, or <c>null</c> when the lease is not valid or the file is no longer the one the job was claimed for.</returns>
    Task<HlsIdentifyResponse?> IdentifyAsync(Guid jobId, HlsIdentification identification, CancellationToken cancellationToken);

    /// <summary>
    /// Checks whether a worker may upload to a root of an HLS store: the server can read it and trust it, and a local one is
    /// an existing directory outside the libraries and the data of the server.
    /// </summary>
    /// <param name="root">The root: <c>s3://bucket/prefix</c>, or an absolute path on the server.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result.</returns>
    Task<HlsStoreTestResult> CheckBaseAsync(string root, CancellationToken cancellationToken);

    /// <summary>
    /// Records the renditions a worker encoded; they are published once verified.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="completion">The renditions.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the lease was valid and the job has a plan.</returns>
    Task<bool> CompleteAsync(Guid jobId, HlsCompletion completion, CancellationToken cancellationToken);

    /// <summary>
    /// Ends a job a worker gives up; it is tried again until it used up its attempts.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="failure">Why.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>Whether the lease was valid.</returns>
    Task<bool> FailAsync(Guid jobId, HlsFailure failure, CancellationToken cancellationToken);

    /// <summary>
    /// Gets the state of pre-encoded HLS.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The state.</returns>
    Task<HlsStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Gets the most recently changed jobs.
    /// </summary>
    /// <param name="limit">The most jobs to return, from 1 to 1000.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The jobs.</returns>
    Task<IReadOnlyList<HlsJobSummary>> GetJobsAsync(int limit, CancellationToken cancellationToken);

    /// <summary>
    /// Asks for a rebuild of the work list: pending jobs are cancelled, failed renditions become missing again and everything is
    /// scheduled again; published renditions are kept.
    /// </summary>
    void RequestRebuild();

    /// <summary>
    /// Starts verifying published renditions again, in the background.
    /// </summary>
    /// <param name="itemId">The item whose content is verified, or <c>null</c> for every content.</param>
    /// <returns>Whether it started; <c>false</c> while it is running already.</returns>
    bool StartReverify(Guid? itemId);
}
