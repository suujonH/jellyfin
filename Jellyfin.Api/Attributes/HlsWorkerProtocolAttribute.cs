using System;
using System.Globalization;
using Jellyfin.Api.Extensions;
using MediaBrowser.Model.Hls;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Api.Attributes;

/// <summary>
/// Admits only HLS workers: requests with an API key, never a user session, that speak the protocol of the server.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class HlsWorkerProtocolAttribute : ActionFilterAttribute
{
    private static readonly string _version = HlsWorkerProtocol.Version.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.HttpContext.User.GetIsApiKey())
        {
            context.Result = new ObjectResult("HLS workers authenticate with an API key.") { StatusCode = StatusCodes.Status403Forbidden };
        }
        else if (!string.Equals(context.HttpContext.Request.Headers[HlsWorkerProtocol.Header], _version, StringComparison.Ordinal))
        {
            context.Result = new BadRequestObjectResult("The server speaks version " + _version + " of the HLS worker protocol, in header " + HlsWorkerProtocol.Header + ".");
        }
    }
}
