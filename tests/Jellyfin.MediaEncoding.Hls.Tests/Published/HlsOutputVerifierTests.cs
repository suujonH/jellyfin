using System;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Model.Hls;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Published;

public class HlsOutputVerifierTests
{
    private static readonly DateTime _written = new(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);

    private static readonly HlsOutputFile[] _reported = [new("init.mp4", 900), new("segment-000000.m4s", 1000)];

    [Fact]
    public void Compare_EveryFileInPlace_IsNoProblem()
        // A file nobody reported, such as one a worker left behind, is not checked.
        => Assert.Null(HlsOutputVerifier.Compare(_reported, [Stored("segment-000000.m4s", 1000), Stored("init.mp4", 900), Stored("extra.tmp", 5)]));

    [Fact]
    public void Compare_NothingListed_IsAMissingDirectory()
        => Assert.Equal("The directory is missing or empty.", HlsOutputVerifier.Compare(_reported, []));

    [Fact]
    public void Compare_AFileMissing_NamesIt()
        => Assert.Equal("The file segment-000000.m4s is missing.", HlsOutputVerifier.Compare(_reported, [Stored("init.mp4", 900)]));

    [Fact]
    public void Compare_AFileOfAnotherLength_NamesBothLengths()
        => Assert.Equal("The file init.mp4 is 899 bytes long, not 900.", HlsOutputVerifier.Compare(_reported, [Stored("init.mp4", 899), Stored("segment-000000.m4s", 1000)]));

    [Fact]
    public void Compare_NamesAreCaseSensitive()
        => Assert.Equal("The file init.mp4 is missing.", HlsOutputVerifier.Compare(_reported, [Stored("INIT.MP4", 900), Stored("segment-000000.m4s", 1000)]));

    private static HlsStoreFile Stored(string path, long length) => new(path, length, _written);
}
