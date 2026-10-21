using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
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
/// The API HLS workers claim, encode and upload jobs of pre-encoded HLS with. Every request carries an API key and the header
/// <c>X-Jellyfin-Hls-Protocol: 1</c>. A request that does not make sense is answered with 400, a lease that is no longer valid
/// with 409, and a store that cannot be used with 503, each with the reason.
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
    /// Gets the file of a job: a local file, with ranges, or a redirect to the target of a .strm file, signed for the worker.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="leaseToken">The token of the lease.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">File returned.</response>
    /// <response code="302">Redirected to the storage.</response>
    /// <response code="404">The file is missing.</response>
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
    /// <param name="identification">The hash and the ffprobe output of the file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The plan, or that the job is done.</response>
    /// <response code="400">The hash or the probe is not valid, or the job was identified as other content.</response>
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
    /// Gets where to upload files of a rendition of the plan.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="request">The rendition and its files.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Addresses returned.</response>
    /// <response code="400">The plan has no such rendition or file.</response>
    /// <response code="409">The lease is no longer valid.</response>
    /// <returns>The addresses.</returns>
    [HttpPost("Jobs/{jobId}/Uploads")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult<HlsUploadResponse>> GetUploads(
        [FromRoute, Required] Guid jobId,
        [FromBody, Required] HlsUploadRequest request,
        CancellationToken cancellationToken)
        => GuardAsync<HlsUploadResponse>(async () =>
        {
            if (await _taskCenter.GetUploadTargetsAsync(jobId, request, cancellationToken).ConfigureAwait(false) is not { } targets)
            {
                return Conflict(LeaseLost);
            }

            return new HlsUploadResponse(targets
                .Select(t => ToAddress(
                    t.Path,
                    t.Address,
                    t.ContentType,
                    "HlsWorker/Jobs/" + jobId.ToString("N") + "/Files/" + Uri.EscapeDataString(request.Key) + "/" + Uri.EscapeDataString(t.Path)
                        + "?leaseToken=" + Uri.EscapeDataString(request.LeaseToken)))
                .ToList());
        });

    /// <summary>
    /// Uploads a file of a rendition to a local HLS store.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="key">The key of the rendition.</param>
    /// <param name="file">The name of the file.</param>
    /// <param name="leaseToken">The token of the lease.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">File written.</response>
    /// <response code="400">The plan has no such rendition or file, or the store is network storage.</response>
    /// <response code="409">The lease is no longer valid.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPut("Jobs/{jobId}/Files/{key}/{file}")]
    [DisableRequestSizeLimit]
    [AcceptsFile(MediaTypeNames.Application.Octet)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult> PutFile(
        [FromRoute, Required] Guid jobId,
        [FromRoute, Required] string key,
        [FromRoute, Required] string file,
        [FromQuery, Required] string leaseToken,
        CancellationToken cancellationToken)
        => GuardAsync(async () =>
            await _taskCenter.WriteFileAsync(jobId, leaseToken, key, file, Request.Body, cancellationToken).ConfigureAwait(false) ? NoContent() : Conflict(LeaseLost));

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

    /// <summary>
    /// Gets where a worker uploads a test file, to see that its uploads reach the HLS store. The file is deleted afterwards.
    /// </summary>
    /// <param name="request">The worker.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Address returned.</response>
    /// <response code="503">No store is set, or no access covers it.</response>
    /// <returns>The address.</returns>
    [HttpPost("UploadCheck")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<ActionResult<HlsUploadAddress>> StartUploadCheck([FromBody, Required] HlsWorkerRequest request, CancellationToken cancellationToken)
        => GuardAsync<HlsUploadAddress>(async () =>
        {
            var check = await _taskCenter.StartUploadCheckAsync(request.Worker, cancellationToken).ConfigureAwait(false);
            return ToAddress(check.Path, check.Address, MediaTypeNames.Application.Octet, "HlsWorker/UploadCheck/" + check.Id.ToString("N"));
        });

    /// <summary>
    /// Uploads the test file of an upload check to a local HLS store, which writes and deletes it.
    /// </summary>
    /// <param name="checkId">The check.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">File written and deleted.</response>
    /// <response code="404">The check is not known, or it expired.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPut("UploadCheck/{checkId}")]
    [DisableRequestSizeLimit]
    [AcceptsFile(MediaTypeNames.Application.Octet)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult> PutUploadCheck([FromRoute, Required] Guid checkId, CancellationToken cancellationToken)
        => GuardAsync(async () =>
            await _taskCenter.WriteUploadCheckAsync(checkId, Request.Body, cancellationToken).ConfigureAwait(false) ? NoContent() : NotFound("The upload check is not known, or it expired."));

    // A signed address in the storage, or the route of the server for a local store.
    private static HlsUploadAddress ToAddress(string path, Uri? address, string contentType, string serverRoute)
        => new(path, "PUT", address?.AbsoluteUri ?? serverRoute, new Dictionary<string, string> { ["Content-Type"] = contentType });

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
