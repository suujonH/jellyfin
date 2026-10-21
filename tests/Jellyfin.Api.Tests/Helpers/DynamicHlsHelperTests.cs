using Jellyfin.Api.Helpers;
using MediaBrowser.Controller.Streaming;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Jellyfin.Api.Tests.Helpers
{
    public static class DynamicHlsHelperTests
    {
        [Theory]
        [MemberData(nameof(GetVttTimestampMapMpegts_Success_TestData))]
        public static void GetVttTimestampMapMpegts_Success(string? segmentContainer, bool isLiveStream, long expected)
        {
            Assert.Equal(expected, DynamicHlsHelper.GetVttTimestampMapMpegts(segmentContainer, isLiveStream));
        }

        public static TheoryData<string?, bool, long> GetVttTimestampMapMpegts_Success_TestData()
        {
            var data = new TheoryData<string?, bool, long>();
            data.Add(null, false, 900000);
            data.Add(string.Empty, false, 900000);
            data.Add("ts", false, 900000);
            data.Add("mp4", false, 900000);
            data.Add("MP4", false, 900000);
            data.Add(null, true, 900000);
            data.Add("ts", true, 900000);
            data.Add("mp4", true, 0);
            data.Add("MP4", true, 0);
            return data;
        }

        [Theory]
        [InlineData("?audioCodec=aac", "h264")]
        [InlineData("?audioCodec=aac&allowVideoStreamCopy=true&videoCodec=", "h264")]
        [InlineData("?ALLOWVIDEOSTREAMCOPY=true&audioCodec=aac&VideoCodec=hevc,h264", "hevc,h264")]
        public static void DisableVideoStreamCopy_Query_AsksForATranscode(string query, string videoCodec)
        {
            var parameters = QueryHelpers.ParseQuery(DynamicHlsHelper.DisableVideoStreamCopy(new QueryString(query)).Value);

            Assert.Equal(3, parameters.Count);
            Assert.Equal("aac", parameters["audioCodec"]);
            Assert.Equal("false", parameters["allowVideoStreamCopy"]);
            Assert.Equal(videoCodec, parameters["videoCodec"]);
        }

        [Theory]
        [InlineData(null, "h264")]
        [InlineData("", "h264")]
        [InlineData("hevc,h264", "hevc,h264")]
        public static void DisableVideoStreamCopy_Request_AsksForATranscode(string? requestedCodec, string videoCodec)
        {
            // The state has taken the first of the codecs by then.
            var request = new StreamingRequestDto { AllowVideoStreamCopy = true, VideoCodec = "hevc" };

            DynamicHlsHelper.DisableVideoStreamCopy(request, requestedCodec);

            Assert.False(request.AllowVideoStreamCopy);
            Assert.Equal(videoCodec, request.VideoCodec);
        }
    }
}
