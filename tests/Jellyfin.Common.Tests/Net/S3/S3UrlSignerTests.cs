using System;
using System.Collections.Generic;
using System.Globalization;
using MediaBrowser.Common.Net.S3;
using Xunit;

namespace Jellyfin.Common.Tests.Net.S3;

public class S3UrlSignerTests
{
    private static readonly DateTime _signingTime = DateTime.Parse("2013-05-24T00:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

    private static S3Access ExampleBucket(string? endpoint = "https://s3.amazonaws.com", bool pathStyle = false)
        => new(endpoint, "us-east-1", pathStyle, "AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", null);

    [Fact]
    public void GetUrl_AwsExample_MatchesTheDocumentedSignature()
    {
        // The presigned GET example of the AWS Signature Version 4 documentation.
        var url = S3UrlSigner.GetUrl(ExampleBucket(), new S3Address("examplebucket", "test.txt"), _signingTime, TimeSpan.FromDays(1));

        Assert.Equal(
            "https://examplebucket.s3.amazonaws.com/test.txt"
            + "?X-Amz-Algorithm=AWS4-HMAC-SHA256"
            + "&X-Amz-Credential=AKIAIOSFODNN7EXAMPLE%2F20130524%2Fus-east-1%2Fs3%2Faws4_request"
            + "&X-Amz-Date=20130524T000000Z"
            + "&X-Amz-Expires=86400"
            + "&X-Amz-SignedHeaders=host"
            + "&X-Amz-Signature=aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404",
            url.AbsoluteUri);
    }

    [Fact]
    public void GetUrl_PathStyle_PutsTheBucketInThePath()
    {
        var url = S3UrlSigner.GetUrl(ExampleBucket("http://minio.lan:9000", true), new S3Address("examplebucket", "a/b.mkv"), _signingTime, TimeSpan.FromHours(1));

        Assert.StartsWith("http://minio.lan:9000/examplebucket/a/b.mkv?", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Expires=3600&", url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void GetUrl_NoEndpoint_UsesAwsInTheRegion()
    {
        var bucket = ExampleBucket(null) with { Region = "eu-west-1" };

        var url = S3UrlSigner.GetUrl(bucket, new S3Address("examplebucket", "test.txt"), _signingTime, TimeSpan.FromHours(1));

        Assert.StartsWith("https://examplebucket.s3.eu-west-1.amazonaws.com/test.txt?", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("%2Feu-west-1%2Fs3%2Faws4_request", url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void GetUrl_KeyWithSpacesAndNonAscii_IsPercentEncodedByteWise()
    {
        var url = S3UrlSigner.GetUrl(ExampleBucket(), new S3Address("examplebucket", "电影/My Film (2020).mkv"), _signingTime, TimeSpan.FromHours(1));

        Assert.StartsWith(
            "https://examplebucket.s3.amazonaws.com/%E7%94%B5%E5%BD%B1/My%20Film%20%282020%29.mkv?",
            url.AbsoluteUri,
            StringComparison.Ordinal);
    }

    [Fact]
    public void GetUrl_LifetimeOverSevenDays_IsClamped()
    {
        var url = S3UrlSigner.GetUrl(ExampleBucket(), new S3Address("examplebucket", "test.txt"), _signingTime, TimeSpan.FromDays(30));

        Assert.Contains("X-Amz-Expires=604800&", url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void GetUrl_SessionToken_IsSigned()
    {
        var bucket = ExampleBucket() with { SessionToken = "token/with+chars=" };

        var url = S3UrlSigner.GetUrl(bucket, new S3Address("examplebucket", "test.txt"), _signingTime, TimeSpan.FromDays(1));

        Assert.Contains("&X-Amz-Security-Token=token%2Fwith%2Bchars%3D&X-Amz-SignedHeaders=host&", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.DoesNotContain("aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404", url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void GetUrl_NoAccessKey_IsNotSigned()
    {
        var bucket = ExampleBucket() with { AccessKey = null };

        var url = S3UrlSigner.GetUrl(bucket, new S3Address("examplebucket", "test.txt"), _signingTime, TimeSpan.FromDays(1));

        Assert.Equal("https://examplebucket.s3.amazonaws.com/test.txt", url.AbsoluteUri);
    }

    [Fact]
    public void GetUrl_Head_IsSignedForHead()
    {
        // Signature Version 4 signs the method: an address presigned for GET is refused for HEAD.
        var get = S3UrlSigner.GetUrl(ExampleBucket(), "examplebucket", "test.txt", _signingTime, TimeSpan.FromDays(1), "GET", null);
        var head = S3UrlSigner.GetUrl(ExampleBucket(), "examplebucket", "test.txt", _signingTime, TimeSpan.FromDays(1), "HEAD", null);

        Assert.EndsWith("aeeed9bbccd4d02ee5c0109b86d86835f995330da4c265957d157751f604d404", get.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal(get.AbsoluteUri[..get.AbsoluteUri.LastIndexOf('=')], head.AbsoluteUri[..head.AbsoluteUri.LastIndexOf('=')]);
        Assert.NotEqual(get.AbsoluteUri, head.AbsoluteUri);
    }

    [Fact]
    public void GetUrl_BucketRootWithParameters_SignsThemInCanonicalOrder()
    {
        var parameters = new Dictionary<string, string> { ["max-keys"] = "1", ["list-type"] = "2" };

        var url = S3UrlSigner.GetUrl(ExampleBucket("http://minio.lan:9000", true), "examplebucket", string.Empty, _signingTime, TimeSpan.FromMinutes(5), "GET", parameters);

        Assert.StartsWith("http://minio.lan:9000/examplebucket/?X-Amz-Algorithm=", url.AbsoluteUri, StringComparison.Ordinal);
        Assert.Contains("&X-Amz-SignedHeaders=host&list-type=2&max-keys=1&X-Amz-Signature=", url.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public void GetUrl_UnsignedWithParameters_KeepsThem()
    {
        var parameters = new Dictionary<string, string> { ["list-type"] = "2" };

        var url = S3UrlSigner.GetUrl(ExampleBucket() with { AccessKey = null }, "examplebucket", string.Empty, _signingTime, TimeSpan.FromMinutes(5), "GET", parameters);

        Assert.Equal("https://examplebucket.s3.amazonaws.com/?list-type=2", url.AbsoluteUri);
    }

    [Theory]
    [InlineData("s3://bucket/key.mkv", "bucket", "key.mkv")]
    [InlineData("S3://my.bucket-1/a/b c/电影.mkv", "my.bucket-1", "a/b c/电影.mkv")]
    [InlineData("s3://bucket/a%20b.mkv", "bucket", "a%20b.mkv")]
    public void TryParse_Valid_SplitsBucketAndKey(string path, string bucket, string key)
    {
        Assert.True(S3Address.TryParse(path, out var address));
        Assert.Equal(bucket, address.Bucket);
        Assert.Equal(key, address.Key);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://bucket/key.mkv")]
    [InlineData("s3://")]
    [InlineData("s3://bucket")]
    [InlineData("s3://bucket/")]
    [InlineData("s3:///key.mkv")]
    [InlineData("s3://user@bucket/key.mkv")]
    public void TryParse_Invalid_ReturnsFalse(string? path)
    {
        Assert.False(S3Address.TryParse(path, out _));
    }
}
