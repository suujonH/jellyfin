using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.NetworkStorage;
using MediaBrowser.Controller.NetworkStorage;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.NetworkStorage;

public class HttpStorageProtocolTests
{
    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    public async Task CheckAsync_HeadRefused_IsDecidedByARangedGet(HttpStatusCode headStatus)
    {
        // An S3 address presigned for GET refuses HEAD, since the method is signed.
        var handler = new StubHandler(request => request.Method == HttpMethod.Head
            ? new HttpResponseMessage(headStatus)
            : new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent([0]) { Headers = { ContentRange = new ContentRangeHeaderValue(0, 0, 1000) } }
            });
        var protocol = CreateProtocol(handler);
        Assert.True(protocol.TryLocate("https://bucket.s3.example.com/Film.mkv?X-Amz-Signature=abc", out var location));

        var result = await protocol.CheckAsync(null, location, 1000, CancellationToken.None);

        Assert.Equal(ObjectCheckStatus.Ok, result.Status);
        Assert.Equal([HttpMethod.Head, HttpMethod.Get], handler.Methods);
        Assert.Equal("bytes=0-0", handler.LastRange);
    }

    [Fact]
    public async Task CheckAsync_GetForbiddenToo_IsForbidden()
    {
        var protocol = CreateProtocol(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        Assert.True(protocol.TryLocate("https://cdn.example.com/Film.mkv", out var location));

        var result = await protocol.CheckAsync(null, location, null, CancellationToken.None);

        Assert.Equal(ObjectCheckStatus.Forbidden, result.Status);
    }

    private static HttpStorageProtocol CreateProtocol(StubHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        return new HttpStorageProtocol(factory.Object);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpMethod> Methods { get; } = [];

        public string? LastRange { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method);
            LastRange = request.Headers.Range?.ToString();
            return Task.FromResult(respond(request));
        }
    }
}
