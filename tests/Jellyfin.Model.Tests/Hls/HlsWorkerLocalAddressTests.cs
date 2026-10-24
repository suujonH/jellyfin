using System;
using MediaBrowser.Model.Hls;
using Xunit;

namespace Jellyfin.Model.Tests.Hls;

public class HlsWorkerLocalAddressTests
{
    private static readonly Guid _worker = Guid.Parse("0123456789abcdef0123456789abcdef");

    [Theory]
    [InlineData("worker-local://0123456789abcdef0123456789abcdef/D:\\Movies\\a.mkv", "D:\\Movies\\a.mkv")]
    [InlineData("worker-local://0123456789abcdef0123456789abcdef//srv/movies/a.mkv", "/srv/movies/a.mkv")]
    [InlineData("WORKER-LOCAL://0123456789ABCDEF0123456789ABCDEF/a.mkv", "a.mkv")]
    public void TryParse_ReadsTheWorkerAndThePath(string value, string path)
    {
        Assert.True(HlsWorkerLocalAddress.TryParse(value, out var worker, out var localPath));
        Assert.Equal(_worker, worker);
        Assert.Equal(path, localPath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("s3://bucket/a.mkv")]
    [InlineData("worker-local://0123456789abcdef0123456789abcdef")]
    [InlineData("worker-local://0123456789abcdef0123456789abcdef/")]
    [InlineData("worker-local://0123456789abcdef0123456789abcdefD:\\a.mkv")]
    [InlineData("worker-local://01234567-89ab-cdef-0123-456789abcdef/a.mkv")]
    [InlineData("worker-local://00000000000000000000000000000000/a.mkv")]
    [InlineData("worker-local://0123456789abcdef0123456789abcdef/a\nb.mkv")]
    public void TryParse_RefusesWhatIsNotSuchATarget(string? value)
    {
        Assert.False(HlsWorkerLocalAddress.TryParse(value, out var worker, out var localPath));
        Assert.Equal(Guid.Empty, worker);
        Assert.Empty(localPath);
    }

    [Fact]
    public void Format_IsReadBack_AndNeedsAWorkerAndAPath()
    {
        var target = HlsWorkerLocalAddress.Format(_worker, "/srv/a.mkv");

        Assert.Equal("worker-local://0123456789abcdef0123456789abcdef//srv/a.mkv", target);
        Assert.True(HlsWorkerLocalAddress.TryParse(target, out var worker, out var path));
        Assert.Equal((_worker, "/srv/a.mkv"), (worker, path));
        Assert.Throws<ArgumentException>(() => HlsWorkerLocalAddress.Format(Guid.Empty, "/srv/a.mkv"));
        Assert.Throws<ArgumentException>(() => HlsWorkerLocalAddress.Format(_worker, string.Empty));
        Assert.Throws<ArgumentException>(() => HlsWorkerLocalAddress.Format(_worker, "a\rb"));
    }
}
