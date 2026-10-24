using System.Collections.Generic;
using System.Globalization;
using System.Security.Claims;
using Jellyfin.Api.Attributes;
using Jellyfin.Api.Constants;
using MediaBrowser.Model.Hls;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Jellyfin.Api.Tests.Attributes;

public class HlsWorkerProtocolAttributeTests
{
    private static readonly string _version = HlsWorkerProtocol.Version.ToString(CultureInfo.InvariantCulture);

    [Fact]
    public void AWorkerWithAnApiKey_SpeakingTheProtocol_IsAdmitted()
        => Assert.Null(Run(isApiKey: true, _version));

    [Theory]
    [InlineData(null)]
    [InlineData("1")]
    [InlineData("2")]
    public void AnotherProtocol_IsRefused(string? version)
        => Assert.Equal(StatusCodes.Status400BadRequest, Assert.IsType<BadRequestObjectResult>(Run(isApiKey: true, version)).StatusCode);

    [Fact]
    public void AUserSession_IsRefused()
        => Assert.Equal(StatusCodes.Status403Forbidden, Assert.IsType<ObjectResult>(Run(isApiKey: false, _version)).StatusCode);

    private static IActionResult? Run(bool isApiKey, string? version)
    {
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(InternalClaimTypes.IsApiKey, isApiKey.ToString())], "Test"))
        };
        if (version is not null)
        {
            httpContext.Request.Headers[HlsWorkerProtocol.Header] = version;
        }

        var context = new ActionExecutingContext(
            new ActionContext(httpContext, new RouteData(), new ActionDescriptor()),
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: null!);
        new HlsWorkerProtocolAttribute().OnActionExecuting(context);
        return context.Result;
    }
}
