using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Controller.NetworkStorage;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Hls;

/// <summary>
/// A store over a fake bucket <c>media</c>, which network storage lists and deletes in.
/// </summary>
public class NetworkHlsStoreTests
{
    private const string Published = "0123456789abcdef0123456789abcdef/video/1080/fedcba9876543210fedcba9876543210";
    private const string Other = "0123456789abcdef0123456789abcdef/audio/1/00112233445566778899aabbccddeeff";

    private readonly SortedDictionary<string, byte[]> _bucket = new(StringComparer.Ordinal);
    private readonly Mock<INetworkStorageManager> _networkStorage = new();
    private readonly List<string[]> _deleted = [];

    public NetworkHlsStoreTests()
    {
        _networkStorage.Setup(n => n.GetServerAddress(It.IsAny<string>(), It.IsAny<string>()))
            .Returns((string target, string method) => Address(target, method));
        _networkStorage.Setup(n => n.ListAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .Returns((string prefix, int? limit, CancellationToken _) => Task.FromResult(List(prefix, limit)));
        _networkStorage.Setup(n => n.DeleteAsync(It.IsAny<IReadOnlyCollection<string>>(), It.IsAny<CancellationToken>()))
            .Returns((IReadOnlyCollection<string> targets, CancellationToken _) =>
            {
                _deleted.Add(targets.ToArray());
                foreach (var target in targets)
                {
                    _bucket.Remove(Key(target));
                }

                return Task.CompletedTask;
            });
    }

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CheckRoot_NoAccess_IsAProblem()
    {
        _networkStorage.Setup(n => n.GetServerAddress(It.IsAny<string>(), It.IsAny<string>())).Returns((Uri?)null);

        Assert.Equal("No access covers bucket media of the HLS store.", await CreateStore().CheckRootAsync([], CancellationToken));
    }

    [Fact]
    public async Task CheckRoot_Archive_IsAProblem()
    {
        _networkStorage.Setup(n => n.IsArchived("s3://media/hls/.jellyfin-hls")).Returns(true);

        Assert.Equal("The access of bucket media of the HLS store is an archive.", await CreateStore().CheckRootAsync([], CancellationToken));
    }

    [Theory]
    [InlineData("hls/", "hls/.jellyfin-hls")]
    [InlineData("hls/", "hls/.distributed-hls")]
    [InlineData("", ".jellyfin-hls")]
    public async Task CheckRoot_Marked_IsUsableWhateverIsPublished(string prefix, string marker)
    {
        _bucket[marker] = [1];

        Assert.Null(await CreateStore(prefix).CheckRootAsync([Published, Other], CancellationToken));
        Assert.Equal([marker], _bucket.Keys);
        Assert.True(await CreateStore(prefix).HasMarkerAsync(CancellationToken));
    }

    [Fact]
    public async Task CheckRoot_NothingPublished_IsUsableAndNotMarked()
    {
        Assert.Null(await CreateStore().CheckRootAsync([], CancellationToken));
        Assert.Empty(_bucket);
    }

    [Fact]
    public async Task CheckRoot_Unmarked_IsUsableWhileAPublishedDirectoryIsThere_AndIsNotMarked()
    {
        // The marker is for the worker to write.
        _bucket["hls/" + Published + "/init.mp4"] = [1];

        Assert.Null(await CreateStore().CheckRootAsync([Other, Published], CancellationToken));
        Assert.Equal(["hls/" + Published + "/init.mp4"], _bucket.Keys);
        Assert.False(await CreateStore().HasMarkerAsync(CancellationToken));
    }

    [Fact]
    public async Task CheckRoot_UnmarkedWithoutAPublishedDirectory_IsAProblemAndIsNotMarked()
    {
        // Objects left by a test, or a directory whose name starts with a published one, are neither.
        _bucket["hls/.jellyfin-hls-test-1"] = [1];
        _bucket["hls/" + Published + "x/init.mp4"] = [1];

        Assert.Equal(
            "The HLS store has no marker .jellyfin-hls and none of the published renditions; its access may name other storage: s3://media/hls",
            await CreateStore().CheckRootAsync([Published], CancellationToken));
        Assert.False(_bucket.ContainsKey("hls/.jellyfin-hls"));
    }

    [Fact]
    public async Task CheckRoot_ListingFails_IsAProblem()
    {
        _networkStorage.Setup(n => n.ListAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Listing s3://media/hls/.jellyfin-hls failed: 503 SlowDown"));

        Assert.Equal(
            "The HLS store cannot be used: Listing s3://media/hls/.jellyfin-hls failed: 503 SlowDown",
            await CreateStore().CheckRootAsync([Published], CancellationToken));
    }

    [Fact]
    public async Task ListAsync_Directory_ListsTheFilesUnderItRelativeToIt()
    {
        _bucket["hls/" + Published + "/"] = [];
        _bucket["hls/" + Published + "/init.mp4"] = [1, 2, 3];
        _bucket["hls/" + Published + "/segment-000000.m4s"] = [1];
        _bucket["hls/" + Published + "x/init.mp4"] = [1];

        var files = await CreateStore().ListAsync(Published, CancellationToken);

        // The key of the directory itself is a folder some tools create.
        Assert.Equal(["init.mp4", "segment-000000.m4s"], files.Select(f => f.Path));
        Assert.Equal([3L, 1L], files.Select(f => f.Length));
    }

    [Fact]
    public async Task DeleteDirectory_Directory_DeletesEveryObjectUnderIt()
    {
        _bucket["hls/" + Published + "/"] = [];
        _bucket["hls/" + Published + "/init.mp4"] = [1];
        _bucket["hls/" + Published + "/segment-000000.m4s"] = [1];
        _bucket["hls/" + Other + "/init.mp4"] = [1];

        await CreateStore().DeleteDirectoryAsync(Published, CancellationToken);

        Assert.Equal(["hls/" + Other + "/init.mp4"], _bucket.Keys);
        Assert.Equal("s3://media/hls/" + Published + "/init.mp4", Assert.Single(_deleted)[1]);

        // Gone already.
        await CreateStore().DeleteDirectoryAsync(Published, CancellationToken);
        Assert.Single(_deleted);
    }

    [Fact]
    public void Addresses_File_AreThoseOfItsObject()
    {
        const string Target = "s3://media/hls/" + Published + "/init.mp4";
        _networkStorage.Setup(n => n.ResolveShortcutTarget(Target)).Returns("https://media.example.com/server");
        _networkStorage.Setup(n => n.GetClientAddress(Target)).Returns(new Uri("https://media.example.com/client"));
        var store = CreateStore();

        Assert.Equal("https://media.example.com/server", store.GetServerPath(Published + "/init.mp4"));
        Assert.Equal(new Uri("https://media.example.com/client"), store.GetClientAddress(Published + "/init.mp4"));
        Assert.Throws<ArgumentException>(() => store.GetClientAddress("../x"));
        Assert.Equal("s3://media/hls", store.Location);
        Assert.Equal("s3://media", CreateStore(string.Empty).Location);
    }

    [Fact]
    public void GetServerPath_Archive_IsNotPlayed()
    {
        const string Target = "s3://media/hls/" + Published + "/init.mp4";
        _networkStorage.Setup(n => n.ResolveShortcutTarget(Target)).Returns("https://media.example.com/server");
        _networkStorage.Setup(n => n.IsArchived(Target)).Returns(true);

        Assert.Null(CreateStore().GetServerPath(Published + "/init.mp4"));
    }

    private static Uri Address(string target, string method)
        => new("http://minio.lan:9000/" + target["s3://".Length..] + "?method=" + method);

    private static string Key(string target)
    {
        Assert.StartsWith("s3://media/", target, StringComparison.Ordinal);
        return target["s3://media/".Length..];
    }

    private IReadOnlyList<StorageObject> List(string prefix, int? limit)
    {
        var key = Key(prefix);
        return _bucket
            .Where(o => o.Key.StartsWith(key, StringComparison.Ordinal))
            .Take(limit ?? int.MaxValue)
            .Select(o => new StorageObject(o.Key, o.Value.Length, DateTime.UnixEpoch))
            .ToArray();
    }

    private NetworkHlsStore CreateStore(string prefix = "hls/") => new(_networkStorage.Object, "media", prefix);
}
