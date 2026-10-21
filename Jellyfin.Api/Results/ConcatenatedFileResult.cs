using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Api.Results;

/// <summary>
/// Sends files one after another as one response, such as the GOP files of a live HLS segment.
/// </summary>
public class ConcatenatedFileResult : ActionResult
{
    private readonly IReadOnlyList<string> _paths;
    private readonly string _contentType;

    /// <summary>
    /// Initializes a new instance of the <see cref="ConcatenatedFileResult"/> class.
    /// </summary>
    /// <param name="paths">The files, in order; none may change while they are sent.</param>
    /// <param name="contentType">The content type of the response.</param>
    public ConcatenatedFileResult(IReadOnlyList<string> paths, string contentType)
    {
        _paths = paths;
        _contentType = contentType;
    }

    /// <inheritdoc />
    public override async Task ExecuteResultAsync(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var response = context.HttpContext.Response;
        var lengths = _paths.Select(p => new FileInfo(p).Length).ToList();
        response.ContentType = _contentType;
        response.ContentLength = lengths.Sum();
        for (var i = 0; i < _paths.Count; i++)
        {
            await response.SendFileAsync(_paths[i], 0, lengths[i], context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
