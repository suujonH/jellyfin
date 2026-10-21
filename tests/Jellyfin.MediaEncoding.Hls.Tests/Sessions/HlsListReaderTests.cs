using System;
using System.Globalization;
using System.IO;
using Jellyfin.MediaEncoding.Hls.Sessions;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Sessions;

public sealed class HlsListReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "jellyfin-hls-list-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture) + ".csv");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Parse_Line_ReadsTheTimesToTheMicrosecond()
    {
        var entry = HlsListReader.Parse("run-12.ts,12.345678,19.800001");

        Assert.Equal(new HlsListEntry("run-12.ts", 123_456_780, 198_000_010), entry);
    }

    [Theory]
    [InlineData("run-0.ts")]
    [InlineData("run-0.ts,1.000000")]
    [InlineData(",1.000000,2.000000")]
    [InlineData("run-0.ts,a,2.000000")]
    [InlineData("run-0.ts,1.000000,")]
    public void Parse_InvalidLine_Throws(string line)
    {
        Assert.Throws<InvalidDataException>(() => HlsListReader.Parse(line));
    }

    [Fact]
    public void Read_NoFile_ReadsNothing()
    {
        var reader = new HlsListReader(_path);

        Assert.False(reader.Read());
        Assert.Empty(reader.Entries);
    }

    [Fact]
    public void Read_LineWrittenInParts_IsReadOnceItEnds()
    {
        var reader = new HlsListReader(_path);
        File.WriteAllText(_path, "run-0.ts,0.000000,1.7");

        Assert.False(reader.Read());
        Assert.Empty(reader.Entries);

        File.AppendAllText(_path, "00000\nrun-1.ts,1.700000,4.200000\r\nrun-2.ts,4.2");
        Assert.True(reader.Read());
        Assert.Equal(
            [new HlsListEntry("run-0.ts", 0, 17_000_000), new HlsListEntry("run-1.ts", 17_000_000, 42_000_000)],
            reader.Entries);

        Assert.False(reader.Read());
        File.AppendAllText(_path, "00000,6.100000\n");
        Assert.True(reader.Read());
        Assert.Equal(new HlsListEntry("run-2.ts", 42_000_000, 61_000_000), reader.Entries[2]);
    }

    [Fact]
    public void Read_FileHeldOpenByTheWriter_IsRead()
    {
        var reader = new HlsListReader(_path);
        using var writer = new StreamWriter(new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete));
        writer.Write("run-0.ts,0.000000,1.000000\n");
        writer.Flush();

        Assert.True(reader.Read());
        Assert.Single(reader.Entries);
    }
}
