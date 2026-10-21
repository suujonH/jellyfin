using System;
using System.Globalization;
using Jellyfin.MediaEncoding.Hls.Plans;
using Jellyfin.MediaEncoding.Hls.Playlist;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Playlist;

public class HlsPlaylistWriterTests
{
    [Fact]
    public void WriteMedia_TransportStream_WritesVersion3WithoutMap()
    {
        var playlist = HlsPlaylistWriter.WriteMedia([60_060_000, 60_060_000, 25_000_000], null, i => $"hls1/abc/{i}.ts?x=1");

        Assert.Equal(
            "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:7\n#EXT-X-MEDIA-SEQUENCE:0\n"
            + "#EXTINF:6.006000, nodesc\nhls1/abc/0.ts?x=1\n"
            + "#EXTINF:6.006000, nodesc\nhls1/abc/1.ts?x=1\n"
            + "#EXTINF:2.500000, nodesc\nhls1/abc/2.ts?x=1\n"
            + "#EXT-X-ENDLIST\n",
            playlist.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void WriteMedia_Fmp4_WritesVersion7WithMap()
    {
        var playlist = HlsPlaylistWriter.WriteMedia([60_000_000], "hls1/abc/-1.mp4?x=1", i => $"hls1/abc/{i}.mp4?x=1");

        Assert.Equal(
            "#EXTM3U\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-VERSION:7\n#EXT-X-TARGETDURATION:6\n#EXT-X-MEDIA-SEQUENCE:0\n"
            + "#EXT-X-MAP:URI=\"hls1/abc/-1.mp4?x=1\"\n"
            + "#EXTINF:6.000000, nodesc\nhls1/abc/0.mp4?x=1\n"
            + "#EXT-X-ENDLIST\n",
            playlist.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void WriteMedia_Plan_WritesItsSegments()
    {
        var plan = SegmentPlanner.CreateUniformPlan(TimeSpan.FromSeconds(13).Ticks, TimeSpan.FromSeconds(6).Ticks, null);

        var playlist = HlsPlaylistWriter.WriteMedia(plan, null, i => i.ToString(CultureInfo.InvariantCulture));

        Assert.Contains("#EXTINF:6.000000, nodesc\n0\n#EXTINF:6.000000, nodesc\n1\n#EXTINF:1.000000, nodesc\n2\n", playlist.ReplaceLineEndings("\n"), StringComparison.Ordinal);
    }
}
