using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Attributes;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Model.Hls;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The API HLS workers claim and report jobs of pre-encoded HLS with; they upload to their own storage. Every request carries an
/// API key and the header <c>X-Jellyfin-Hls-Protocol: 1</c>. A request that does not make sense is answered with 400, a lease that
/// is no longer valid with 409, and storage that cannot be reached with 503, each with the reason.
/// </summary>
[Route("HlsWorker")]
[Authorize(Policy = Policies.RequiresElevation)]
[HlsWorkerProtocol]
public class HlsWorkerController : BaseJellyfinApiController
{
    private const string LeaseLost = "The lease of the job is no longer valid.";

    private readonly IHlsTaskCenter _taskCenter;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsWorkerController"/> class.
    /// </summary>
    /// <param name="taskCenter">The task center of pre-encoded HLS.</param>
    public HlsWorkerController(IHlsTaskCenter taskCenter)
    {
        _taskCenter = taskCenter;
    }

    /// <summary>
    /// Gets the version of the worker protocol the server speaks.
    /// </summary>
    /// <response code="200">Version returned.</response>
    /// <returns>The version.</returns>
    [HttpGet("Protocol")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<HlsProtocolVersion> GetProtocol() => new HlsProtocolVersion(HlsWorkerProtocol.Version);

    /// <summary>
    /// Claims the oldest job the worker can do.
    /// </summary>
    /// <param name="request">The worker.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Job claimed.</response>
    /// <response code="204">No job is waiting, or pre-encoded HLS is off.</response>
    /// <returns>The job.</returns>
    [HttpPost("Poll")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<ActionResult<HlsWorkAssignment>> Poll([FromBody, Required] HlsWorkerRequest request, CancellationToken cancellationToken)
        => GuardAsync<HlsWorkAssignment>(async () =>
        {
            if (await _taskCenter.PollAsync(request.Worker, cancellationToken).ConfigureAwait(false) is not { } job)
            {
                return NoContent();
            }

            // Relative to the address of the server, so the path of a reverse proxy is kept.
            var inputUrl = "HlsWorker/Jobs/" + job.Lease.JobId.ToString("N") + "/Input?leaseToken=" + Uri.EscapeDataString(job.Lease.LeaseToken);
            return new HlsWorkAssignment(job.Lease, job.Source, inputUrl);
        });

    /// <summary>
    /// Gets the libraries whose files are pre-encoded, with the video qualities a worker adding media may encode right away.
    /// </summary>
    /// <response code="200">Libraries returned; none while pre-encoded HLS is off.</response>
    /// <returns>The libraries.</returns>
    [HttpGet("Libraries")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<HlsWorkerLibrary>> GetLibraries() => Ok(_taskCenter.GetLibraries());

    /// <summary>
    /// Starts a job for media the worker adds to a library, before the file is in it, leased to the worker at once.
    /// </summary>
    /// <param name="request">The media, the root of the HLS store and the qualities to encode now.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The job and its plan, or that nothing asked for is left to encode.</response>
    /// <response code="400">The library is not pre-encoded, the qualities are not some of its own, the hash or the probe is not valid, or the worker may not upload to the root.</response>
    /// <response code="409">The content has a job in progress, or another job holds its renditions.</response>
    /// <returns>The job.</returns>
    [HttpPost("Jobs/Eager")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<HlsEagerResponse>> StartEagerJob([FromBody, Required] HlsEagerRequest request, CancellationToken cancellationToken)
        => GuardAsync<HlsEagerResponse>(async () =>
            await _taskCenter.StartEagerJobAsync(request, cancellationToken).ConfigureAwait(false) is { } response
                ? response
                : Conflict("The content has a job in progress, or another job holds its renditions."));

    /// <summary>
    /// Gets the file of a job: a local file, with ranges, or a redirect to the target of a .strm file, signed for the worker.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="leaseToken">The token of the lease.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">File returned.</response>
    /// <response code="302">Redirected to the storage.</response>
    /// <response code="404">The file is missing, or the job was started for media being added and reads the file of its worker.</response>
    /// <response code="409">The lease is no longer valid.</response>
    /// <returns>The file.</returns>
    [HttpGet("Jobs/{jobId}/Input")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesFile(MediaTypeNames.Application.Octet)]
    public Task<ActionResult> GetInput([FromRoute, Required] Guid jobId, [FromQuery, Required] string leaseToken, CancellationToken cancellationToken)
        => GuardAsync(async () =>
        {
            var input = await _taskCenter.GetInputAsync(jobId, leaseToken, cancellationToken).ConfigureAwait(false);
            if (input is null)
            {
                return Conflict(LeaseLost);
            }

            if (input.Address is { } address)
            {
                return Redirect(address.AbsoluteUri);
            }

            if (input.FilePath is null)
            {
                return NotFound("The job was started for media being added; it reads the file of its worker.");
            }

            return System.IO.File.Exists(input.FilePath)
                ? PhysicalFile(input.FilePath, MediaTypeNames.Application.Octet, true)
                : NotFound("The file " + input.FilePath + " is missing.");
        });

    /// <summary>
    /// Extends the lease of a job.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="request">The lease.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Lease extended.</response>
    /// <response code="409">The lease is no longer valid.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Jobs/{jobId}/Heartbeat")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult> Heartbeat([FromRoute, Required] Guid jobId, [FromBody, Required] HlsLeaseRequest request, CancellationToken cancellationToken)
        => GuardAsync(async () => await _taskCenter.HeartbeatAsync(jobId, request.LeaseToken, cancellationToken).ConfigureAwait(false) ? NoContent() : Conflict(LeaseLost));

    /// <summary>
    /// Records what the file of a job is, and gets what the job encodes.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="identification">The hash and the ffprobe output of the file, and the root of the HLS store the worker uploads to.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The plan, or that the job is done.</response>
    /// <response code="400">The hash or the probe is not valid, the worker may not upload to the root, or the job was identified as other content or with another root.</response>
    /// <response code="409">The lease is no longer valid, or the file changed since the claim.</response>
    /// <returns>What the job encodes.</returns>
    [HttpPost("Jobs/{jobId}/Identify")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<HlsIdentifyResponse>> Identify(
        [FromRoute, Required] Guid jobId,
        [FromBody, Required] HlsIdentification identification,
        CancellationToken cancellationToken)
        => GuardAsync<HlsIdentifyResponse>(async () =>
            await _taskCenter.IdentifyAsync(jobId, identification, cancellationToken).ConfigureAwait(false) is { } response
                ? response
                : Conflict("The lease of the job is no longer valid, or its file changed since it was claimed."));

    /// <summary>
    /// Checks whether the worker may upload to a root of an HLS store, as identifying a job does.
    /// </summary>
    /// <param name="request">The root.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Checked; the result says whether the root can be used and has the marker.</response>
    /// <returns>The result.</returns>
    [HttpPost("Bases/Check")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<HlsStoreTestResult>> CheckBase([FromBody, Required] HlsBaseRequest request, CancellationToken cancellationToken)
        => await _taskCenter.CheckBaseAsync(request.Base, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Reports the renditions of a job as encoded and uploaded; they are published once verified.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="completion">The renditions.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="202">Outputs recorded, waiting to be verified.</response>
    /// <response code="400">The outputs do not agree with the plan.</response>
    /// <response code="409">The lease is no longer valid.</response>
    /// <returns>An <see cref="AcceptedResult"/>.</returns>
    [HttpPost("Jobs/{jobId}/Complete")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult> Complete([FromRoute, Required] Guid jobId, [FromBody, Required] HlsCompletion completion, CancellationToken cancellationToken)
        => GuardAsync(async () => await _taskCenter.CompleteAsync(jobId, completion, cancellationToken).ConfigureAwait(false) ? Accepted() : Conflict(LeaseLost));

    /// <summary>
    /// Gives up a job; it is tried again until it used up its attempts.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="failure">Why.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Job released.</response>
    /// <response code="409">The lease is no longer valid.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Jobs/{jobId}/Failed")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult> Fail([FromRoute, Required] Guid jobId, [FromBody, Required] HlsFailure failure, CancellationToken cancellationToken)
        => GuardAsync(async () => await _taskCenter.FailAsync(jobId, failure, cancellationToken).ConfigureAwait(false) ? NoContent() : Conflict(LeaseLost));

    private async Task<ActionResult> GuardAsync(Func<Task<ActionResult>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (IOException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
    }

    private async Task<ActionResult<T>> GuardAsync<T>(Func<Task<ActionResult<T>>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (IOException ex)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ex.Message);
        }
    }
}
