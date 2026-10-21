using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Jellyfin.MediaEncoding.Keyframes.FfProbe
{
    public class FfProbeKeyframeExtractorTests
    {
        [Theory]
        [InlineData("keyframes.txt", "keyframes_result.json")]
        [InlineData("keyframes_streamduration.txt", "keyframes_streamduration_result.json")]
        public void ParseStream_Valid_Success(string testDataFileName, string resultFileName)
        {
            var testDataPath = Path.Combine("FfProbe/Test Data", testDataFileName);
            var resultPath = Path.Combine("FfProbe/Test Data", resultFileName);
            using var resultFileStream = File.OpenRead(resultPath);
            var expectedResult = JsonSerializer.Deserialize<KeyframeData>(resultFileStream)!;

            using var fileStream = File.OpenRead(testDataPath);
            using var streamReader = new StreamReader(fileStream);

            var result = FfProbeKeyframeExtractor.ParseStream(streamReader);

            Assert.Equal(expectedResult.TotalDuration, result.TotalDuration);
            Assert.Equal(expectedResult.KeyframeTicks, result.KeyframeTicks);
        }

        [Fact]
        public void ParseStream_FormatStartTime_IsRemovedFromTheKeyframes()
        {
            // MPEG-TS, whose timestamps start at 1.462 s; ffprobe prints the format section after the packets.
            const string Output = "packet,1.483000,K__,\npacket,1.608000,___,\npacket,3.193000,K__,\npacket,5.696000,K__,\n"
                + "program,stream,19.978700\nstream,19.978700\nformat,1.462000,19.999700\n";
            using var reader = new StreamReader(new MemoryStream(Encoding.UTF8.GetBytes(Output)));

            var result = FfProbeKeyframeExtractor.ParseStream(reader);

            Assert.Equal(new long[] { 210_000, 17_310_000, 42_340_000 }, result.KeyframeTicks);
            Assert.Equal(TimeSpan.FromSeconds(19.9787).Ticks, result.TotalDuration);
        }
    }
}
