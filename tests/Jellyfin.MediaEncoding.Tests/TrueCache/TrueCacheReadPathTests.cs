using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.MediaEncoding.Encoder;
using MediaBrowser.MediaEncoding.Subtitles;
using MediaBrowser.MediaEncoding.TrueCache;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.TrueCache
{
    /// <summary>
    /// The encoder, the subtitle encoder and the probe result of a declared resource never touch the resource.
    /// None of the encoders below has an ffmpeg or ffprobe path, so any attempt to run one fails the test.
    /// </summary>
    public sealed class TrueCacheReadPathTests : IDisposable
    {
        private readonly TrueCacheFixture _fixture = new();
        private readonly TrueCacheService _trueCache;

        public TrueCacheReadPathTests()
        {
            var config = new Mock<IServerConfigurationManager>();
            config.SetupGet(c => c.Configuration).Returns(new ServerConfiguration { TrickplayOptions = new TrickplayOptions { WidthResolutions = [320] } });
            _trueCache = new TrueCacheService(NullLogger<TrueCacheService>.Instance, Mock.Of<ILocalizationManager>(), config.Object);
        }

        public void Dispose() => _fixture.Dispose();

        [Fact]
        public async Task GetMediaInfo_DeclaredFile_ReturnsDeclaredProbeResult()
        {
            var path = _fixture.CreateCompleteVideo();

            var result = await CreateEncoder().GetMediaInfo(CreateRequest(path, true), CancellationToken.None);

            Assert.Equal("mkv", result.Container);
            Assert.Equal(TimeSpan.FromSeconds(100).Ticks, result.RunTimeTicks);
            Assert.Equal(2, result.Chapters.Length);
        }

        [Fact]
        public async Task GetMediaInfo_WithoutChapters_LeavesThemOut()
        {
            var path = _fixture.CreateCompleteVideo();

            var result = await CreateEncoder().GetMediaInfo(CreateRequest(path, false), CancellationToken.None);

            Assert.Empty(result.Chapters);
        }

        [Fact]
        public async Task GetMediaInfo_RejectedFile_IsNotRead()
        {
            var path = _fixture.CreateResource("Film.mkv");
            var files = TrueCacheFixture.CompleteVideoFiles();
            files.Remove(TrueCacheLayout.ProbeFile);
            _fixture.WriteEntry(path, files);

            await Assert.ThrowsAsync<TrueCacheRejectedException>(() => CreateEncoder().GetMediaInfo(CreateRequest(path, true), CancellationToken.None));
        }

        [Fact]
        public async Task ExtractVideoImage_DeclaredFile_IsRefused()
        {
            var path = _fixture.CreateCompleteVideo();

            await Assert.ThrowsAsync<TrueCacheRejectedException>(() => CreateEncoder().ExtractVideoImage(
                path,
                "mkv",
                new MediaSourceInfo { Protocol = MediaProtocol.File },
                new MediaStream { Type = MediaStreamType.Video },
                null,
                TimeSpan.FromSeconds(10),
                CancellationToken.None));
        }

        [Fact]
        public async Task GetReadableFile_DeclaredEmbeddedSubtitle_ReturnsDeclaredExtraction()
        {
            var path = _fixture.CreateCompleteVideo();
            var streams = _trueCache.Lookup(path).Entry!.CreateMediaInfo(true).MediaStreams;
            var mediaSource = new MediaSourceInfo { Id = Guid.NewGuid().ToString("N"), Path = path, Protocol = MediaProtocol.File, MediaStreams = streams };
            var encoder = new SubtitleEncoder(
                NullLogger<SubtitleEncoder>.Instance,
                Mock.Of<IFileSystem>(),
                Mock.Of<IMediaEncoder>(MockBehavior.Strict),
                Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
                Mock.Of<IMediaSourceManager>(MockBehavior.Strict),
                Mock.Of<ISubtitleParser>(MockBehavior.Strict),
                Mock.Of<IPathManager>(MockBehavior.Strict),
                Mock.Of<IServerConfigurationManager>(),
                _trueCache);

            var subtitle = await encoder.GetReadableFile(mediaSource, streams.Single(s => s.Type == MediaStreamType.Subtitle), CancellationToken.None);

            Assert.Equal("ass", subtitle.Format);
            Assert.Equal(Path.Combine(TrueCacheLayout.GetEntryDirectory(path)!, "subtitles", "2.ass"), subtitle.Path);
        }

        private static MediaInfoRequest CreateRequest(string path, bool chapters) => new()
        {
            ExtractChapters = chapters,
            MediaType = DlnaProfileType.Video,
            MediaSource = new MediaSourceInfo { Path = path, Protocol = MediaProtocol.File, VideoType = VideoType.VideoFile }
        };

        private MediaEncoder CreateEncoder() => new(
            Mock.Of<ILogger<MediaEncoder>>(),
            Mock.Of<IServerConfigurationManager>(),
            Mock.Of<IFileSystem>(),
            Mock.Of<IBlurayExaminer>(),
            Mock.Of<ILocalizationManager>(),
            new ConfigurationBuilder().Build(),
            Mock.Of<IServerConfigurationManager>(),
            _trueCache);
    }
}
