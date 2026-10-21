using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Jellyfin.Server.Implementations.NetworkStorage;
using MediaBrowser.Common.Net.S3;
using MediaBrowser.Model.NetworkStorage;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.NetworkStorage;

public class S3StorageProtocolTests
{
    private static readonly NetworkStorageAccessDto _access = new()
    {
        Protocol = NetworkStorageProtocols.S3,
        Bucket = "media",
        Endpoint = "http://minio.lan:9000",
        UsePathStyle = true,
        AccessKey = "key",
        SecretKey = "secret"
    };

    [Fact]
    public async Task ListAsync_TruncatedPages_FollowsTheContinuationToken()
    {
        var handler = new StubHandler(request => request.RequestUri!.Query.Contains("continuation-token=", StringComparison.Ordinal)
            ? Xml("<ListBucketResult><IsTruncated>false</IsTruncated>"
                + "<Contents><Key>hls/b/init.mp4</Key><LastModified>2026-10-22T13:00:00Z</LastModified><Size>5</Size></Contents></ListBucketResult>")
            : Xml("<ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>media</Name><Prefix>hls/</Prefix>"
                + "<IsTruncated>true</IsTruncated><NextContinuationToken>a+b/c=</NextContinuationToken>"
                + "<Contents><Key>hls/a/init.mp4</Key><LastModified>2026-10-22T12:00:00.000Z</LastModified><ETag>\"x\"</ETag><Size>1200</Size></Contents>"
                + "<Contents><Key>hls/a/segment-000000.m4s</Key><LastModified>2026-10-22T12:00:01.500Z</LastModified><Size>654321</Size></Contents>"
                + "</ListBucketResult>"));

        var objects = await CreateProtocol(handler).ListAsync(_access, Locate("s3://media/hls/"), null, TestContext.Current.CancellationToken);

        Assert.Equal(["hls/a/init.mp4", "hls/a/segment-000000.m4s", "hls/b/init.mp4"], objects.Select(o => o.Key));
        Assert.Equal([1200L, 654321L, 5L], objects.Select(o => o.Size));
        Assert.Equal(new DateTime(2026, 10, 22, 12, 0, 1, 500, DateTimeKind.Utc), objects[1].LastModified);
        Assert.Equal(DateTimeKind.Utc, objects[1].LastModified.Kind);

        // The bucket root is listed, by prefix.
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Equal("/media/", r.Uri.AbsolutePath));
        Assert.Contains("list-type=2&max-keys=1000&prefix=hls%2F", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("continuation-token", handler.Requests[0].Uri.Query, StringComparison.Ordinal);
        Assert.Contains("continuation-token=a%2Bb%2Fc%3D", handler.Requests[1].Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAsync_Limit_AsksForNoMore()
    {
        var handler = new StubHandler(_ => Xml("<ListBucketResult><IsTruncated>true</IsTruncated><NextContinuationToken>t</NextContinuationToken>"
            + "<Contents><Key>hls/.jellyfin-hls</Key><LastModified>2026-10-22T12:00:00Z</LastModified><Size>1</Size></Contents>"
            + "<Contents><Key>hls/.jellyfin-hls-test-1</Key><LastModified>2026-10-22T12:00:00Z</LastModified><Size>1</Size></Contents>"
            + "</ListBucketResult>"));

        var objects = await CreateProtocol(handler).ListAsync(_access, Locate("s3://media/hls/.jellyfin-hls"), 1, TestContext.Current.CancellationToken);

        Assert.Equal("hls/.jellyfin-hls", Assert.Single(objects).Key);
        Assert.Contains("max-keys=1&", Assert.Single(handler.Requests).Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListAsync_Refused_ThrowsWithTheMessageOfTheStorage()
    {
        var handler = new StubHandler(_ => Xml("<Error><Code>AccessDenied</Code><Message>Access Denied</Message></Error>", HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<IOException>(
            () => CreateProtocol(handler).ListAsync(_access, Locate("s3://media/hls/"), null, TestContext.Current.CancellationToken));

        Assert.Equal("Listing s3://media/hls/ failed: 403 AccessDenied: Access Denied", ex.Message);
    }

    [Theory]
    [InlineData("<ListBucketResult><IsTruncated>true</IsTruncated></ListBucketResult>")]
    [InlineData("<ListBucketResult><Contents><Key>a</Key><LastModified>2026-10-22T12:00:00Z</LastModified></Contents></ListBucketResult>")]
    [InlineData("<html>")]
    [InlineData("<html xmlns=\"http://www.w3.org/1999/xhtml\"><body>Sign in</body></html>")]
    public async Task ListAsync_NoListing_Throws(string body)
    {
        // A truncated listing without a token would list its first page again and again.
        var handler = new StubHandler(_ => Xml(body));

        await Assert.ThrowsAsync<IOException>(
            () => CreateProtocol(handler).ListAsync(_access, Locate("s3://media/hls/"), null, TestContext.Current.CancellationToken));
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DeleteAsync_ManyKeys_DeletesThemAThousandAtATime()
    {
        var handler = new StubHandler(_ => Xml("<DeleteResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"/>"));
        var keys = Enumerable.Range(0, 1001).Select(i => "hls/a/segment-" + i.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".m4s").ToArray();

        await CreateProtocol(handler).DeleteAsync(_access, "media", keys, TestContext.Current.CancellationToken);

        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/media/", request.Uri.AbsolutePath);
            Assert.Contains("&delete=&", request.Uri.Query, StringComparison.Ordinal);
            Assert.Equal(MD5.HashData(request.Body), request.ContentMd5);
            Assert.Equal("true", Element(XElement.Parse(Encoding.UTF8.GetString(request.Body)), "Quiet").Single().Value);
        });
        Assert.Equal(keys, handler.Requests.SelectMany(r => Element(XElement.Parse(Encoding.UTF8.GetString(r.Body)), "Object")).Select(o => o.Value));
        Assert.Equal(1000, Element(XElement.Parse(Encoding.UTF8.GetString(handler.Requests[0].Body)), "Object").Count());
    }

    [Fact]
    public async Task DeleteAsync_KeyWithLineBreaks_IsSentAsItIs()
    {
        var handler = new StubHandler(_ => Xml("<DeleteResult/>"));

        await CreateProtocol(handler).DeleteAsync(_access, "media", ["a\r\nb\rc"], TestContext.Current.CancellationToken);

        Assert.Equal("a\r\nb\rc", Element(XElement.Parse(Encoding.UTF8.GetString(handler.Requests[0].Body)), "Object").Single().Value);
    }

    [Fact]
    public async Task DeleteAsync_KeyFailing_Throws()
    {
        var handler = new StubHandler(_ => Xml("<DeleteResult><Deleted><Key>a</Key></Deleted>"
            + "<Error><Key>b</Key><Code>AccessDenied</Code><Message>Access Denied</Message></Error></DeleteResult>"));

        var ex = await Assert.ThrowsAsync<IOException>(
            () => CreateProtocol(handler).DeleteAsync(_access, "media", ["a", "b"], TestContext.Current.CancellationToken));

        Assert.Equal("Deleting s3://media/b failed: AccessDenied: Access Denied", ex.Message);
    }

    [Fact]
    public async Task DeleteAsync_NoResult_Throws()
    {
        // Such as the login page of a proxy in front of the storage.
        var handler = new StubHandler(_ => Xml("<html><body>Sign in</body></html>"));

        await Assert.ThrowsAsync<IOException>(
            () => CreateProtocol(handler).DeleteAsync(_access, "media", ["a"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteAsync_KeyMissing_IsDeletedAlready()
    {
        var handler = new StubHandler(_ => Xml("<DeleteResult><Error><Key>b</Key><Code>NoSuchKey</Code><Message>gone</Message></Error></DeleteResult>"));

        await CreateProtocol(handler).DeleteAsync(_access, "media", ["b"], TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.MethodNotAllowed)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task DeleteAsync_BatchesRefused_DeletesEachObject(HttpStatusCode batchStatus)
    {
        // Missing objects are deleted already.
        var handler = new StubHandler(request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(batchStatus)
            : new HttpResponseMessage(request.RequestUri!.AbsolutePath.EndsWith('1') ? HttpStatusCode.NotFound : HttpStatusCode.NoContent));
        var keys = Enumerable.Range(0, 1002).Select(i => "hls/" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray();

        await CreateProtocol(handler).DeleteAsync(_access, "media", keys, TestContext.Current.CancellationToken);

        // Once refused, the batches are not asked for again.
        Assert.Equal(HttpMethod.Post, handler.Requests[0].Method);
        Assert.Equal(keys.Select(k => "/media/" + k), handler.Requests.Skip(1).Select(r => r.Uri.AbsolutePath));
        Assert.All(handler.Requests.Skip(1), r => Assert.Equal(HttpMethod.Delete, r.Method));
    }

    [Fact]
    public async Task DeleteAsync_ObjectRefused_Throws()
    {
        var handler = new StubHandler(request => request.Method == HttpMethod.Post
            ? new HttpResponseMessage(HttpStatusCode.NotImplemented)
            : Xml("<Error><Code>AccessDenied</Code><Message>Access Denied</Message></Error>", HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<IOException>(
            () => CreateProtocol(handler).DeleteAsync(_access, "media", ["hls/a", "hls/b"], TestContext.Current.CancellationToken));

        Assert.Equal("Deleting s3://media/hls/a failed: 403 AccessDenied: Access Denied", ex.Message);
        Assert.Equal(2, handler.Requests.Count);
    }

    private static IEnumerable<XElement> Element(XElement parent, string name)
        => parent.Elements().Where(e => e.Name.LocalName == name);

    private static MediaBrowser.Controller.NetworkStorage.StorageLocation Locate(string target)
    {
        Assert.True(new S3StorageProtocol(null!).TryLocate(target, out var location));
        return location;
    }

    private static HttpResponseMessage Xml(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };

    private static S3StorageProtocol CreateProtocol(StubHandler handler)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        return new S3StorageProtocol(factory.Object);
    }

    private sealed record SentRequest(HttpMethod Method, Uri Uri, byte[] Body, byte[]? ContentMd5);

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<SentRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new SentRequest(request.Method, request.RequestUri!, body, request.Content?.Headers.ContentMD5));
            return respond(request);
        }
    }
}
