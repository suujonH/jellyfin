using System;
using System.IO;
using System.Net.Http;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Controller.NetworkStorage;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Hls;

public class HlsStoreFactoryTests
{
    private readonly HlsStoreFactory _factory = new(Mock.Of<INetworkStorageManager>(), Mock.Of<IHttpClientFactory>());

    [Theory]
    [InlineData("s3://media", "s3://media")]
    [InlineData("s3://media/", "s3://media")]
    [InlineData("s3://media/hls", "s3://media/hls")]
    [InlineData(" s3://media/hls/a/ ", "s3://media/hls/a")]
    public void Create_S3Address_IsANetworkStore(string location, string expected)
    {
        var store = _factory.Create(location);

        Assert.IsType<NetworkHlsStore>(store);
        Assert.False(store.IsLocal);
        Assert.Equal(expected, store.Location);
    }

    [Fact]
    public void Create_AbsolutePath_IsALocalStore()
    {
        var path = Path.Combine(Path.GetTempPath(), "hls") + Path.DirectorySeparatorChar;

        var store = _factory.Create(path);

        Assert.IsType<LocalHlsStore>(store);
        Assert.True(store.IsLocal);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "hls"), store.Location);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData("hls")]
    [InlineData("./hls")]
    [InlineData("s3://")]
    [InlineData("s3:///hls")]
    [InlineData("s3://bad bucket/hls")]
    [InlineData("s3://media/a//b")]
    [InlineData("s3://media/../b")]
    public void Create_NeitherAnAbsolutePathNorAnS3Address_IsRefused(string location)
    {
        Assert.Throws<ArgumentException>(() => _factory.Create(location));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("0123/video/1080/abcd/segment-000000.m4s")]
    [InlineData(".jellyfin-hls")]
    public void Validate_RelativePath_IsAccepted(string path)
    {
        HlsStorePaths.Validate(path, nameof(path));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/a")]
    [InlineData("a/")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("a/../b")]
    [InlineData("..")]
    [InlineData("a\\b")]
    [InlineData("C:/a")]
    [InlineData("...")]
    [InlineData(".. ")]
    [InlineData("a/b.")]
    [InlineData("a /b")]
    [InlineData("a\rb")]
    [InlineData("a\u0001")]
    public void Validate_PathLeavingTheStore_IsRefused(string path)
    {
        Assert.Throws<ArgumentException>(() => HlsStorePaths.Validate(path, nameof(path)));
    }
}
