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
/// The API HLS workers claim and report jobs of pre-encoded HLS with; they upload to their own storage, and write the files they
/// add to libraries into the library folders through the server. Every request carries an
/// API key and the header <c>X-Jellyfin-Hls-Protocol: 2</c>. A request that does not make sense is answered with 400, a lease that
/// is no longer valid with 409, and storage that cannot be reached with 503, each with the reason.
/// </summary>
[Route("HlsWorker")]
[Authorize(Policy = Policies.RequiresElevation)]
[HlsWorkerProtocol]
public class HlsWorkerController : BaseJellyfinApiController
{
    private const string LeaseLost = "The lease of the job is no longer valid.";

    private const string UploadLost = "The upload does not exist, or has ended.";

    private readonly IHlsTaskCenter _taskCenter;
    private readonly IHlsLibraryWriter _libraryWriter;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsWorkerController"/> class.
    /// </summary>
    /// <param name="taskCenter">The task center of pre-encoded HLS.</param>
    /// <param name="libraryWriter">The writer of library folders.</param>
    public HlsWorkerController(IHlsTaskCenter taskCenter, IHlsLibraryWriter libraryWriter)
    {
        _taskCenter = taskCenter;
        _libraryWriter = libraryWriter;
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
    /// Registers the worker, under the id it has or a new one; media it keeps on its own machine is named by the id.
    /// </summary>
    /// <param name="registration">The worker.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Worker registered.</response>
    /// <response code="400">The name or the id is not valid.</response>
    /// <returns>The id of the worker.</returns>
    [HttpPost("Workers")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public Task<ActionResult<HlsWorkerRegistered>> RegisterWorker([FromBody, Required] HlsWorkerRegistration registration, CancellationToken cancellationToken)
        => GuardAsync<HlsWorkerRegistered>(async () => new HlsWorkerRegistered(await _taskCenter.RegisterWorkerAsync(registration, cancellationToken).ConfigureAwait(false)));

    /// <summary>
    /// Claims the oldest job the worker can do, of the libraries it asks for and not of the items it excludes.
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
            if (await _taskCenter.PollAsync(request, cancellationToken).ConfigureAwait(false) is not { } job)
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
    /// <response code="404">The file is missing, or the job reads the file of its worker: it was started for media being added, or its file is on the worker.</response>
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
                return NotFound("The file of the job is on its worker.");
            }

            return System.IO.File.Exists(input.FilePath)
                ? PhysicalFile(input.FilePath, MediaTypeNames.Application.Octet, true)
                : NotFound("The file " + input.FilePath + " is missing.");
        });

    /// <summary>
    /// Gives back a job whose file the worker cannot read, before it identified it; the attempt is not counted.
    /// </summary>
    /// <param name="jobId">The job.</param>
    /// <param name="request">The lease.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Job given back.</response>
    /// <response code="409">The lease is no longer valid, or the job was already identified.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Jobs/{jobId}/Return")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult> Return([FromRoute, Required] Guid jobId, [FromBody, Required] HlsLeaseRequest request, CancellationToken cancellationToken)
        => GuardAsync(async () => await _taskCenter.ReturnAsync(jobId, request.LeaseToken, cancellationToken).ConfigureAwait(false)
            ? NoContent()
            : Conflict("The lease of the job is no longer valid, or the job was already identified."));

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

    /// <summary>
    /// Starts writing a file into a folder of a library: a plain file, or the zip of the TrueCache entry of a .strm file.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="request">The folder, the path in it, the size and the worker.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Upload started.</response>
    /// <response code="400">The library or its folder does not exist, a folder must be named, or the path or size is not valid.</response>
    /// <response code="503">The folder cannot be written.</response>
    /// <returns>The upload.</returns>
    [HttpPost("Libraries/{libraryId}/Uploads")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<ActionResult<HlsLibraryUpload>> StartUpload(
        [FromRoute, Required] Guid libraryId,
        [FromBody, Required] HlsLibraryUploadStart request,
        CancellationToken cancellationToken)
        => GuardAsync<HlsLibraryUpload>(async () => new HlsLibraryUpload(await _libraryWriter.StartAsync(libraryId, request, cancellationToken).ConfigureAwait(false)));

    /// <summary>
    /// Writes a chunk of a file, the body of the request: at the end of what was written, or again at an earlier offset.
    /// </summary>
    /// <param name="uploadId">The upload.</param>
    /// <param name="offset">Where the chunk goes in the file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">Chunk written.</response>
    /// <response code="400">The chunk does not follow what was written, is larger than 16 MiB, or goes past the size.</response>
    /// <response code="404">The upload does not exist.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPut("Uploads/{uploadId}")]
    [RequestSizeLimit(17 << 20)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public Task<ActionResult> PutChunk([FromRoute, Required] string uploadId, [FromQuery, Required] long offset, CancellationToken cancellationToken)
        => GuardAsync(async () => await _libraryWriter.WriteAsync(uploadId, offset, Request.Body, cancellationToken).ConfigureAwait(false) ? NoContent() : NotFound(UploadLost));

    /// <summary>
    /// Ends an upload with all of its file and puts the file in place: a plain file replaces what is there, a TrueCache entry goes in
    /// place with its .strm file. The upload ends even when this fails.
    /// </summary>
    /// <param name="uploadId">The upload.</param>
    /// <param name="request">The target of the .strm file, or none for a plain file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">File in place.</response>
    /// <response code="400">The file is not complete, or the entry, the target or the operation is not valid.</response>
    /// <response code="404">The upload does not exist.</response>
    /// <response code="409">The file is held by another operation.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Uploads/{uploadId}/Complete")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public Task<ActionResult> CompleteUpload([FromRoute, Required] string uploadId, [FromBody, Required] HlsLibraryUploadComplete request, CancellationToken cancellationToken)
        => GuardAsync(async () =>
        {
            try
            {
                return await _libraryWriter.CompleteAsync(uploadId, request, cancellationToken).ConfigureAwait(false) ? NoContent() : NotFound(UploadLost);
            }
            catch (HlsWorkingTakenException ex)
            {
                return Conflict(ex.Message);
            }
        });

    /// <summary>
    /// Ends an upload and drops what was written.
    /// </summary>
    /// <param name="uploadId">The upload.</param>
    /// <response code="204">Upload ended.</response>
    /// <response code="404">The upload does not exist.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpDelete("Uploads/{uploadId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult> AbortUpload([FromRoute, Required] string uploadId)
        => await _libraryWriter.AbortAsync(uploadId).ConfigureAwait(false) ? NoContent() : NotFound(UploadLost);

    /// <summary>
    /// Marks a file a worker adds to a folder of a library as held by one of its operations, whatever held it before.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="request">The folder, the path of the file in it and the operation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">File marked; the answer names the operation that held it before, if any.</response>
    /// <response code="400">The library or its folder does not exist, a folder must be named, or the path or the operation is not valid.</response>
    /// <response code="503">The folder cannot be written.</response>
    /// <returns>The operation that held the file.</returns>
    [HttpPut("Libraries/{libraryId}/Working")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<ActionResult<HlsWorkingState>> ClaimWorking(
        [FromRoute, Required] Guid libraryId,
        [FromBody, Required] HlsWorkingClaim request,
        CancellationToken cancellationToken)
        => GuardAsync<HlsWorkingState>(async () => new HlsWorkingState(await _libraryWriter.ClaimWorkingAsync(libraryId, request, cancellationToken).ConfigureAwait(false)));

    /// <summary>
    /// Gets the operation that holds a file a worker adds to a folder of a library.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="request">The folder and the path of the file in it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">The operation, or none when the file is not marked.</response>
    /// <response code="400">The library or its folder does not exist, a folder must be named, or the path is not valid.</response>
    /// <response code="503">The folder cannot be read.</response>
    /// <returns>The operation.</returns>
    [HttpPost("Libraries/{libraryId}/Working/Read")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<ActionResult<HlsWorkingState>> GetWorking(
        [FromRoute, Required] Guid libraryId,
        [FromBody, Required] HlsWorkingPath request,
        CancellationToken cancellationToken)
        => GuardAsync<HlsWorkingState>(async () => new HlsWorkingState(await _libraryWriter.GetWorkingAsync(libraryId, request, cancellationToken).ConfigureAwait(false)));

    /// <summary>
    /// Removes the mark of a file a worker added while it names the operation.
    /// </summary>
    /// <param name="libraryId">The library.</param>
    /// <param name="request">The folder, the path of the file in it and the operation.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="204">The mark is removed, or names another operation and is left.</response>
    /// <response code="400">The library or its folder does not exist, a folder must be named, or the path or the operation is not valid.</response>
    /// <response code="503">The folder cannot be written.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPost("Libraries/{libraryId}/Working/Release")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public Task<ActionResult> ReleaseWorking(
        [FromRoute, Required] Guid libraryId,
        [FromBody, Required] HlsWorkingRelease request,
        CancellationToken cancellationToken)
        => GuardAsync(async () =>
        {
            await _libraryWriter.ReleaseWorkingAsync(libraryId, request, cancellationToken).ConfigureAwait(false);
            return NoContent();
        });

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
