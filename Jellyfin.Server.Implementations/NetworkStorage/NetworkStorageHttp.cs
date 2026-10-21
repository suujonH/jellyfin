using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.NetworkStorage;

namespace Jellyfin.Server.Implementations.NetworkStorage;

/// <summary>
/// Reads the headers of network resources.
/// </summary>
internal static class NetworkStorageHttp
{
    /// <summary>
    /// How long a check waits for an answer before the storage counts as unreachable.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reads whether a resource exists and its size. HEAD is tried first; a server that does not take HEAD, or
    /// refuses it, is asked for the first byte, whose <c>Content-Range</c> gives the size.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="head">The address to send HEAD to.</param>
    /// <param name="get">The address to send a ranged GET to, if HEAD is refused; <c>null</c> to not try.</param>
    /// <param name="expectedSize">The size the resource should have, if known.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The result; a timeout or a network error is transient.</returns>
    public static async Task<ObjectCheckResult> CheckAsync(HttpClient client, Uri head, Uri? get, long? expectedSize, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            using (var request = new HttpRequestMessage(HttpMethod.Head, head))
            using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false))
            {
                // An address presigned for GET, as S3 ones in .strm files are, refuses HEAD because the method is
                // signed; whether it can be read is up to GET.
                if (get is null || response.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented
                    or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden))
                {
                    return ObjectCheckResult.FromStatus((int)response.StatusCode, response.Content.Headers.ContentLength, expectedSize);
                }
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, get))
            {
                request.Headers.Range = new RangeHeaderValue(0, 0);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                var size = response.StatusCode == HttpStatusCode.PartialContent
                    ? response.Content.Headers.ContentRange?.Length
                    : response.Content.Headers.ContentLength;
                return ObjectCheckResult.FromStatus((int)response.StatusCode, size, expectedSize);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ObjectCheckResult(ObjectCheckStatus.Transient, null, null);
        }
        catch (HttpRequestException ex)
        {
            return new ObjectCheckResult(ObjectCheckStatus.Transient, ex.StatusCode is { } status ? (int)status : null, null);
        }
    }
}
