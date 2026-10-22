using System;
using System.Globalization;
using Jellyfin.Api.Extensions;
using Jellyfin.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Api.Attributes;

/// <summary>
/// Redirects a video stream request that names a source of pre-encoded HLS, or a version item, to its master playlist under a new
/// grant, so the clients that build their own stream address play it too. Its arguments are <c>itemId</c>, <c>mediaSourceId</c>
/// and <c>audioStreamIndex</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class HlsPlaybackRedirectAttribute : ActionFilterAttribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="HlsPlaybackRedirectAttribute"/> class. It runs before the automatic validation
    /// of the model (order -2000), so a version item is redirected without the source an action requires.
    /// </summary>
    public HlsPlaybackRedirectAttribute() => Order = -3000;

    /// <inheritdoc />
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.ActionArguments.TryGetValue("itemId", out var value) || value is not Guid itemId)
        {
            return;
        }

        var services = context.HttpContext.RequestServices;
        var principal = context.HttpContext.User;
        var userId = principal.GetUserId();
        var user = userId.IsEmpty() ? null : services.GetRequiredService<IUserManager>().GetUserById(userId);
        if (user is null && !principal.GetIsApiKey())
        {
            return;
        }

        // An item the user does not see is left to the action, which does not find it either. A version item is seen as its
        // original is, which the playlist checks.
        var libraryManager = services.GetRequiredService<ILibraryManager>();
        var hlsPlayback = services.GetRequiredService<IHlsPlaybackService>();
        if (libraryManager.GetItemById(itemId) is not { } item
            || (hlsPlayback.GetOriginalId(item) is null && libraryManager.GetItemById<BaseItem>(itemId, user) is null))
        {
            return;
        }

        var mediaSourceId = context.ActionArguments.TryGetValue("mediaSourceId", out var source) ? source as string : null;
        var audioStreamIndex = context.ActionArguments.TryGetValue("audioStreamIndex", out var audio) ? audio as int? : null;

        // The legacy "params" overrides both, as the stream does when it parses them: the source at position 2, the audio track at 6.
        if (context.ActionArguments.TryGetValue("params", out var legacy) && legacy is string { Length: > 0 } values)
        {
            var parts = values.Split(';');
            if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]))
            {
                mediaSourceId = parts[2];
            }

            if (parts.Length > 6 && int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                audioStreamIndex = index;
            }
        }
        if (hlsPlayback.GetPlaylistUrl(item, mediaSourceId, audioStreamIndex, user) is { } url)
        {
            context.Result = new RedirectResult(context.HttpContext.Request.PathBase + url);
        }
    }
}
