using System;
using System.Threading.Tasks;
using Jellyfin.Api.Controllers;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Api.Tests.Controllers
{
    public class DynamicHlsControllerTests
    {
        [Theory]
        [InlineData(8, 1, "smpte2084", false, "hvc1")]
        [InlineData(8, 1, "smpte2084", true, "hvc1")]
        [InlineData(8, 2, "bt709", false, "hvc1")]
        [InlineData(8, 4, "arib-std-b67", false, "hvc1")]
        [InlineData(8, 6, "smpte2084", false, "hvc1")]
        [InlineData(5, 0, "smpte2084", false, "dvh1")]
        [InlineData(7, 6, "smpte2084", false, "dvh1")]
        [InlineData(null, null, "smpte2084", false, "dvh1")]
        public void GetDolbyVisionHevcCodecTag_SelectsTagForProfile(int? profile, int? compatibilityId, string transfer, bool hdr10Plus, string expected)
        {
            var stream = new MediaStream
            {
                Codec = "hevc",
                Type = MediaStreamType.Video,
                RpuPresentFlag = 1,
                BlPresentFlag = 1,
                ColorSpace = "bt2020nc",
                ColorPrimaries = "bt2020",
                ColorTransfer = transfer,
                DvProfile = profile,
                DvBlSignalCompatibilityId = compatibilityId,
                Hdr10PlusPresentFlag = hdr10Plus
            };

            Assert.Equal(expected, DynamicHlsController.GetDolbyVisionHevcCodecTag(stream));
        }

        [Fact]
        public void GetSessionKey_SameParameters_SameKey()
        {
            var userId = Guid.NewGuid();
            var itemId = Guid.NewGuid();

            Assert.Equal(
                DynamicHlsController.GetSessionKey(userId, true, itemId, new QueryString("?mediaSourceId=a&AudioCodec=aac&runtimeTicks=1&actualSegmentLengthTicks=2")),
                DynamicHlsController.GetSessionKey(userId, true, itemId, new QueryString("?audioCodec=aac&MediaSourceId=a")));
        }

        [Fact]
        public void GetSessionKey_OtherVariant_OtherKey()
        {
            var userId = Guid.NewGuid();
            var itemId = Guid.NewGuid();
            var key = DynamicHlsController.GetSessionKey(userId, true, itemId, new QueryString("?audioCodec=aac"));

            Assert.NotEqual(key, DynamicHlsController.GetSessionKey(userId, true, itemId, new QueryString("?audioCodec=opus")));
            Assert.NotEqual(key, DynamicHlsController.GetSessionKey(userId, false, itemId, new QueryString("?audioCodec=aac")));
            Assert.NotEqual(key, DynamicHlsController.GetSessionKey(Guid.NewGuid(), true, itemId, new QueryString("?audioCodec=aac")));
            Assert.NotEqual(key, DynamicHlsController.GetSessionKey(userId, true, Guid.NewGuid(), new QueryString("?audioCodec=aac")));
        }

        [Fact]
        public async Task ActiveRequestCount_UpdatesAtomically()
        {
            const int RequestCount = 1000;
            var job = new TranscodingJob(NullLogger<TranscodingJob>.Instance);

            await Task.WhenAll(
                Task.Run(() => Parallel.For(0, RequestCount, _ => job.IncrementActiveRequestCount())),
                Task.Run(() => Parallel.For(0, RequestCount, _ => job.DecrementActiveRequestCount())));

            Assert.Equal(0, job.ActiveRequestCount);
        }
    }
}
