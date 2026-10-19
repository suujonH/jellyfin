using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.TrueCache;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Reports on <c>__true_cache__</c> entries.
/// </summary>
[Route("TrueCache")]
[Authorize(Policy = Policies.RequiresElevation)]
public class TrueCacheController : BaseJellyfinApiController
{
    private readonly ITrueCacheService _trueCache;

    /// <summary>
    /// Initializes a new instance of the <see cref="TrueCacheController"/> class.
    /// </summary>
    /// <param name="trueCache">The <c>__true_cache__</c> lookup.</param>
    public TrueCacheController(ITrueCacheService trueCache)
    {
        _trueCache = trueCache;
    }

    /// <summary>
    /// Gets the resources that are left out of the library because their entry is incomplete or invalid.
    /// </summary>
    /// <response code="200">The rejected resources, most recent first.</response>
    /// <returns>The rejected resources.</returns>
    [HttpGet("Rejected")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<TrueCacheRejection>> GetRejected() => Ok(_trueCache.GetRejections());

    /// <summary>
    /// Validates the entry of a resource now, ignoring earlier results.
    /// </summary>
    /// <param name="path">The absolute path of the media file or disc folder.</param>
    /// <response code="200">The validation result.</response>
    /// <response code="400">The path is not absolute.</response>
    /// <returns>The state of the entry and, for a rejected entry, why.</returns>
    [HttpPost("Validate")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult<TrueCacheValidationResult> Validate([FromQuery, Required] string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            return BadRequest("The path must be absolute.");
        }

        var result = _trueCache.Validate(path);
        return Ok(new TrueCacheValidationResult(path, result.State, result.Reason));
    }
}
