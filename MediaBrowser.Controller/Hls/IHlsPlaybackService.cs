using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Hls;

namespace MediaBrowser.Controller.Hls;

/// <summary>
/// Plays pre-encoded HLS: issues the anonymous grants that carry its playback and writes its playlists. An item can be played while
/// pre-encoded HLS is on, its library is chosen and its content has a playable set of published renditions.
/// The original file of such an item is played only as <see cref="IsOriginalAllowed"/> allows.
/// </summary>
public interface IHlsPlaybackService
{
    /// <summary>
    /// Gets a value indicating whether the pre-encoded HLS of an item can be played.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <returns>Whether it can be played.</returns>
    bool IsPublished(Guid itemId);

    /// <summary>
    /// Gets a value indicating whether the current request may play the original file of an item: always when the pre-encoded HLS
    /// of the item cannot be played or the request uses an API key; otherwise when the original is allowed at all, and the request
    /// comes from the local network while that is allowed, or the user may play originals.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="user">The user of the request, if any.</param>
    /// <returns>Whether the original may be played.</returns>
    bool IsOriginalAllowed(Guid itemId, User? user);

    /// <summary>
    /// Gets a value indicating whether "auto" plays the original file of an item for the current request: its pre-encoded HLS can be
    /// played, the original may be played, the request comes from the local network and the original is a local file.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="user">The user of the request, if any.</param>
    /// <returns>Whether "auto" prefers the original.</returns>
    bool PrefersOriginal(BaseItem item, User? user);

    /// <summary>
    /// Arranges the media sources of an item for a user. When the pre-encoded HLS of the item can be played, the list is "auto", the
    /// originals that may be played and are not archived, and the published qualities from the highest down; it is empty when the
    /// user may not play media. Otherwise the originals are kept. Each original, an alternate version too, follows the rules of its
    /// own item, as <see cref="IsOriginalAllowed"/> tells them.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <param name="sources">The original sources of the item, for the user.</param>
    /// <param name="user">The user.</param>
    /// <returns>The sources.</returns>
    IReadOnlyList<MediaSourceInfo> ArrangeMediaSources(BaseItem item, IReadOnlyList<MediaSourceInfo> sources, User user);

    /// <summary>
    /// Gets the quality a media source of pre-encoded HLS of an item plays.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="mediaSourceId">The id of the media source.</param>
    /// <returns>The quality, 0 for "auto", or <c>null</c> when the id is not that of a playable source of pre-encoded HLS of the item.</returns>
    int? GetQuality(Guid itemId, string? mediaSourceId);

    /// <summary>
    /// Prepares a media source of pre-encoded HLS of an item for a playback: it is played from the playlists of a new grant, never
    /// directly or by the encoder of the server, and its subtitles are sent as files. The requested tracks that are published become
    /// its selected ones.
    /// </summary>
    /// <param name="itemId">The item.</param>
    /// <param name="source">The media source.</param>
    /// <param name="playSessionId">The play session.</param>
    /// <param name="audioStreamIndex">The audio track the playback asks for, if any; the default track of the source otherwise.</param>
    /// <param name="subtitleStreamIndex">The subtitle track the playback asks for, if any, or -1 for none.</param>
    /// <param name="serverUrl">The address of the server for the request.</param>
    /// <returns>Whether the source can be played.</returns>
    bool PrepareForPlayback(Guid itemId, MediaSourceInfo source, string playSessionId, int? audioStreamIndex, int? subtitleStreamIndex, string serverUrl);

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
