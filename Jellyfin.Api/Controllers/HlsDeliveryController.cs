using System;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Api.Attributes;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Model.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Controllers;

/// <summary>
/// Delivers pre-encoded HLS. The grant is part of the path, so every relative URI of the playlists carries it and clients need no
/// authentication header. An invalid or expired grant is refused with 403; content that cannot be played is not found.
/// </summary>
[Route("HlsPlayback/{grant}")]
[AllowAnonymous]
public class HlsDeliveryController : BaseJellyfinApiController
{
    // Playlists are written from what is published at each request; the files of a published directory never change.
    private const string PlaylistCacheControl = "no-store";
    private const string FileCacheControl = "private, max-age=86400";

    private readonly IHlsPlaybackService _playback;
    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsDeliveryController"/> class.
    /// </summary>
    /// <param name="playback">The playback of pre-encoded HLS.</param>
    /// <param name="httpClientFactory">The HTTP client factory, for files the server reads from network storage.</param>
    public HlsDeliveryController(IHlsPlaybackService playback, IHttpClientFactory httpClientFactory)
    {
        _playback = playback;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Gets a master playlist: <c>master</c> for every quality or <c>{quality}p</c> for one, with <c>-a{index}</c> for one audio track.
    /// </summary>
    /// <param name="grant">The playback grant.</param>
    /// <param name="playlist">The name of the playlist.</param>
    /// <response code="200">Playlist returned.</response>
    /// <response code="403">The grant is not valid.</response>
    /// <response code="404">The item cannot be played, or the quality or the audio track is not published.</response>
    /// <returns>The playlist.</returns>
    [HttpGet("{playlist}.m3u8")]
    [HttpHead("{playlist}.m3u8", Name = "HeadHlsMasterPlaylist")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesPlaylistFile]
    public ActionResult GetMasterPlaylist([FromRoute, Required] string grant, [FromRoute, Required] string playlist)
    {
        if (_playback.VerifyGrant(grant) is not { } itemId)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        return _playback.GetMasterPlaylist(itemId, playlist) is { } text ? Playlist(text) : NotFound();
    }

    /// <summary>
    /// Gets the media playlist of a published rendition.
    /// </summary>
    /// <param name="grant">The playback grant.</param>
    /// <param name="kind">What the rendition holds: <c>video</c>, <c>audio</c> or <c>subtitles</c>.</param>
    /// <param name="index">The video quality, or the stream index of the audio or subtitle track.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">Playlist returned.</response>
    /// <response code="403">The grant is not valid.</response>
    /// <response code="404">The item cannot be played, or the rendition is not published.</response>
    /// <returns>The playlist.</returns>
    [HttpGet("r/{kind}/{index:int}/index.m3u8")]
    [HttpHead("r/{kind}/{index:int}/index.m3u8", Name = "HeadHlsMediaPlaylist")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesPlaylistFile]
    public async Task<ActionResult> GetMediaPlaylist(
        [FromRoute, Required] string grant,
        [FromRoute, Required] string kind,
        [FromRoute, Required] int index,
        CancellationToken cancellationToken)
    {
        if (_playback.VerifyGrant(grant) is not { } itemId)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!HlsMasterPlaylistWriter.TryParseKindName(kind, out var renditionKind))
        {
            return NotFound();
        }

        return await _playback.GetMediaPlaylistAsync(itemId, renditionKind, index, cancellationToken).ConfigureAwait(false) is { } text
            ? Playlist(text)
            : NotFound();
    }

    /// <summary>
    /// Gets a file of a published rendition, which the server sends: from a local store, or read from network storage.
    /// </summary>
    /// <param name="grant">The playback grant.</param>
    /// <param name="kind">What the rendition holds: <c>video</c>, <c>audio</c> or <c>subtitles</c>.</param>
    /// <param name="index">The video quality, or the stream index of the audio or subtitle track.</param>
    /// <param name="attempt">The directory of the attempt the media playlist names.</param>
    /// <param name="file">The name of the file.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <response code="200">File returned.</response>
    /// <response code="206">Part of the file returned.</response>
    /// <response code="403">The grant is not valid.</response>
    /// <response code="404">The item cannot be played, or the rendition is not published or has no such file.</response>
    /// <response code="416">The range is outside the file.</response>
    /// <response code="502">Network storage could not be read.</response>
    /// <returns>The file.</returns>
    [HttpGet("r/{kind}/{index:int}/{attempt}/{file}")]
    [HttpHead("r/{kind}/{index:int}/{attempt}/{file}", Name = "HeadHlsRenditionFile")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status206PartialContent)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status416RangeNotSatisfiable)]
    [ProducesResponseType(StatusCodes.Status502BadGateway)]
    [ProducesFile("video/mp4", "audio/mp4", "video/iso.segment", "text/vtt")]
    public async Task<ActionResult> GetRenditionFile(
        [FromRoute, Required] string grant,
        [FromRoute, Required] string kind,
        [FromRoute, Required] int index,
        [FromRoute, Required] string attempt,
        [FromRoute, Required] string file,
        CancellationToken cancellationToken)
    {
        if (_playback.VerifyGrant(grant) is not { } itemId)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        if (!HlsMasterPlaylistWriter.TryParseKindName(kind, out var renditionKind)
            || _playback.GetFile(itemId, renditionKind, index, attempt, file) is not { } delivered)
        {
            return NotFound();
        }

        if (delivered.IsLocal)
        {
            if (!System.IO.File.Exists(delivered.Location))
            {
                return NotFound();
            }

            Response.Headers.CacheControl = FileCacheControl;
            return PhysicalFile(delivered.Location, delivered.ContentType, enableRangeProcessing: true);
        }

        return await ProxyAsync(new Uri(delivered.Location), delivered.ContentType, cancellationToken).ConfigureAwait(false);
    }

    private ContentResult Playlist(string text)
    {
        Response.Headers.CacheControl = PlaylistCacheControl;
        return Content(text, MimeTypes.GetMimeType("playlist.m3u8"));
    }

    // Reads a file from network storage and sends it on, with the range the client asked for. The address is signed for GET, so
    // a HEAD request reads it too, and only the headers are sent.
    private async Task<ActionResult> ProxyAsync(Uri address, string contentType, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        if (RangeHeaderValue.TryParse(Request.Headers.Range.ToString(), out var range))
        {
            request.Headers.Range = range;
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClientFactory.CreateClient(NamedClient.Default)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return StatusCode(StatusCodes.Status502BadGateway);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The client timed out.
            return StatusCode(StatusCodes.Status502BadGateway);
        }

        // The response is read while the result is written, so it is disposed with the request.
        Response.RegisterForDispose(response);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return NotFound();
        }

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            Response.Headers.ContentRange = response.Content.Headers.ContentRange?.ToString();
            return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }

        if (!response.IsSuccessStatusCode)
        {
            return StatusCode(StatusCodes.Status502BadGateway);
        }

        Response.Headers.AcceptRanges = "bytes";
        Response.Headers.CacheControl = FileCacheControl;
        if (response.Content.Headers.ContentRange is { } contentRange)
        {
            Response.Headers.ContentRange = contentRange.ToString();
        }

        if (response.Content.Headers.ContentLength is { } length)
        {
            Response.ContentLength = length;
        }

        Response.StatusCode = (int)response.StatusCode;
        return new FileStreamResult(await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false), contentType);
    }
}
