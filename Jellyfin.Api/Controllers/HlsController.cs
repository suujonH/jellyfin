using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Api;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Hls;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Manages pre-encoded HLS: its settings, its state and its jobs.
/// </summary>
[Route("Hls")]
[Authorize(Policy = Policies.RequiresElevation)]
public class HlsController : BaseJellyfinApiController
{
    private readonly IHlsTaskCenter _taskCenter;
    private readonly IConfigurationManager _configurationManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsController"/> class.
    /// </summary>
    /// <param name="taskCenter">The task center of pre-encoded HLS.</param>
    /// <param name="configurationManager">The configuration manager.</param>
    public HlsController(IHlsTaskCenter taskCenter, IConfigurationManager configurationManager)
    {
        _taskCenter = taskCenter;
        _configurationManager = configurationManager;
    }

    /// <summary>
    /// Gets the settings.
    /// </summary>
    /// <response code="200">Settings returned.</response>
    /// <returns>The settings.</returns>
    [HttpGet("Settings")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<HlsOptions> GetSettings() => _configurationManager.GetHlsOptions();

    /// <summary>
    /// Saves the settings. They take effect on the next round of the task center, within five seconds.
    /// </summary>
    /// <param name="options">The settings.</param>
    /// <response code="204">Settings saved.</response>
    /// <response code="400">The settings are not valid.</response>
    /// <returns>A <see cref="NoContentResult"/>.</returns>
    [HttpPut("Settings")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult SaveSettings([FromBody, Required] HlsOptions options)
    {
        try
        {
            _configurationManager.SaveConfiguration(HlsConfigurationExtensions.ConfigurationKey, options);
            return NoContent();
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Gets the state of pre-encoded HLS.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">State returned.</response>
    /// <returns>The state.</returns>
    [HttpGet("Status")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<HlsStatus>> GetStatus(CancellationToken cancellationToken)
        => await _taskCenter.GetStatusAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Gets the most recently changed jobs.
    /// </summary>
    /// <param name="limit">The most jobs to return, from 1 to 1000.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Jobs returned.</response>
    /// <returns>The jobs.</returns>
    [HttpGet("Jobs")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<HlsJobSummary>>> GetJobs([FromQuery] int? limit, CancellationToken cancellationToken)
        => Ok(await _taskCenter.GetJobsAsync(limit ?? 200, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// Rebuilds the work list on the next round of the task center: pending jobs are cancelled, failed renditions become
    /// missing again and everything is scheduled again. Published renditions are kept.
    /// </summary>
    /// <response code="202">Rebuild requested.</response>
    /// <returns>An <see cref="AcceptedResult"/>.</returns>
    [HttpPost("Rebuild")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public ActionResult Rebuild()
    {
        _taskCenter.RequestRebuild();
        return Accepted();
    }

    /// <summary>
    /// Starts verifying published renditions against the store, in the background. A rendition whose files are gone is
    /// retired and encoded again; nothing is retired while the store cannot be trusted.
    /// </summary>
    /// <param name="itemId">The item whose content is verified; none verifies every content.</param>
    /// <response code="202">Verification started; its result is part of the state.</response>
    /// <response code="409">A verification is running already.</response>
    /// <returns>An <see cref="AcceptedResult"/>.</returns>
    [HttpPost("Verify")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult Verify([FromQuery] Guid? itemId)
        => _taskCenter.StartReverify(itemId) ? Accepted() : Conflict("A verification is running already.");
}
