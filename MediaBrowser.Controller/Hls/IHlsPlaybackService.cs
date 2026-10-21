using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Model.Hls;

namespace MediaBrowser.Controller.Hls;

/// <summary>
/// Plays pre-encoded HLS: issues the anonymous grants that carry its playback and writes its playlists. An item can be played while
/// pre-encoded HLS is on, its library is chosen and its content has a playable set of published renditions.
/// </summary>
public interface IHlsPlaybackService
{
    /// <summary>
    /// Issues a grant to play the pre-encoded HLS of an item. The signing key is created and saved when there is none.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>The grant, or <c>null</c> when the item cannot be played.</returns>
    string? IssueGrant(Guid itemId);

    /// <summary>
    /// Reads a grant.
    /// </summary>
    /// <param name="grant">The grant.</param>
    /// <returns>The item, or <c>null</c> when the grant is not valid or has expired.</returns>
    Guid? VerifyGrant(string grant);

    /// <summary>
    /// Writes a master playlist of an item, from what is published now.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="name">The name of the playlist without its extension: <c>master</c> or <c>{quality}p</c>, with <c>-a{index}</c> for one audio track.</param>
    /// <returns>The playlist, or <c>null</c> when the item cannot be played, or the quality or the audio track is not published.</returns>
    string? GetMasterPlaylist(Guid itemId, string name);

    /// <summary>
    /// Writes the media playlist of a published rendition of an item. Its files are given at their direct addresses when clients
    /// read the store of the rendition directly, and otherwise relative to the playlist as <c>{attempt}/{file}</c>, the directory
    /// of the published attempt and the name of the file.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="kind">What the rendition holds.</param>
    /// <param name="index">The video quality, or the stream index of the audio or subtitle track.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The playlist, or <c>null</c> when the item cannot be played or the rendition is not published.</returns>
    Task<string?> GetMediaPlaylistAsync(Guid itemId, HlsRenditionKind kind, int index, CancellationToken cancellationToken);

    /// <summary>
    /// Gets a file of a published rendition of an item, for the server to send.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="kind">What the rendition holds.</param>
    /// <param name="index">The video quality, or the stream index of the audio or subtitle track.</param>
    /// <param name="attempt">The directory of the attempt: the published one, or one it replaced, which is kept for a while.</param>
    /// <param name="name">The name of the file.</param>
    /// <returns>The file, or <c>null</c> when the item cannot be played, the rendition is not published or has no such file, or no access covers its store.</returns>
    HlsDeliveredFile? GetFile(Guid itemId, HlsRenditionKind kind, int index, string attempt, string name);
}
