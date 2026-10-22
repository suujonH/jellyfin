using System;
using System.ComponentModel.DataAnnotations;
using Jellyfin.Api.Extensions;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// The address of a source of pre-encoded HLS, its path in the source list: it leads to the master playlist of its version item
/// under a new grant.
/// </summary>
[Route("HlsPlayback/Versions")]
[Authorize]
public class HlsVersionController : BaseJellyfinApiController
{
    private readonly IHlsPlaybackService _playback;
    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsVersionController"/> class.
    /// </summary>
    /// <param name="playback">The playback of pre-encoded HLS.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="userManager">The user manager.</param>
    public HlsVersionController(IHlsPlaybackService playback, ILibraryManager libraryManager, IUserManager userManager)
    {
        _playback = playback;
        _libraryManager = libraryManager;
        _userManager = userManager;
    }

    /// <summary>
    /// Redirects to the master playlist of a version item.
    /// </summary>
    /// <param name="versionId">The version item.</param>
    /// <param name="audioStreamIndex">Optional. The only audio track of the playlist.</param>
    /// <response code="302">Redirected to the playlist.</response>
    /// <response code="404">The version item, or its original, is not found or cannot be played.</response>
    /// <returns>The redirect.</returns>
    [HttpGet("{versionId}/master.m3u8")]
    [HttpHead("{versionId}/master.m3u8", Name = "HeadHlsVersionPlaylist")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult GetVersionPlaylist([FromRoute, Required] Guid versionId, [FromQuery] int? audioStreamIndex)
    {
        var userId = User.GetUserId();
        var user = userId.IsEmpty() ? null : _userManager.GetUserById(userId);
        if (user is null && !User.GetIsApiKey())
        {
            return NotFound();
        }

        // The version item is seen as its original is, which the playlist checks.
        return _libraryManager.GetItemById(versionId) is { } item
            && _playback.GetOriginalId(item) is not null
            && _playback.GetPlaylistUrl(item, null, audioStreamIndex, user) is { } url
                ? Redirect(Request.PathBase + url)
                : NotFound();
    }
}
