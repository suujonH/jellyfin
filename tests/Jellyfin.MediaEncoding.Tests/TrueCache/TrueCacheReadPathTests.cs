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
using MediaBrowser.MediaEncoding.Attachments;
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
    /// The encoder, the subtitle encoder, the attachment extractor and the probe result of a declared resource never touch the resource.
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
            using var encoder = CreateSubtitleEncoder();

            var subtitle = await encoder.GetReadableFile(mediaSource, streams.Single(s => s.Type == MediaStreamType.Subtitle), CancellationToken.None);

            Assert.Equal("ass", subtitle.Format);
            Assert.Equal(Path.Combine(TrueCacheLayout.GetEntryDirectory(path)!, "subtitles", "2.ass"), subtitle.Path);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task GetReadableFile_SourcePlayedFromAnotherPath_ReturnsDeclaredExtraction(bool shortcut)
        {
            var path = _fixture.CreateCompleteVideo(shortcut ? "Film.strm" : "Film.mkv");
            var mediaSource = CreatePlayedSource(path, shortcut);
            using var encoder = CreateSubtitleEncoder();

            var subtitle = await encoder.GetReadableFile(mediaSource, mediaSource.MediaStreams.Single(s => s.Type == MediaStreamType.Subtitle), CancellationToken.None);

            Assert.Equal(Path.Combine(TrueCacheLayout.GetEntryDirectory(path)!, "subtitles", "2.ass"), subtitle.Path);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ExtractAllAttachments_SourcePlayedFromAnotherPath_CopiesDeclaredAttachments(bool shortcut)
        {
            var path = _fixture.CreateCompleteVideo(shortcut ? "Film.strm" : "Film.mkv");
            var mediaSource = CreatePlayedSource(path, shortcut);
            var output = Path.Combine(_fixture.Root, "attachments-out");
            using var extractor = CreateAttachmentExtractor(mediaSource.Id, output);

            await extractor.ExtractAllAttachments(mediaSource.Path, mediaSource, CancellationToken.None);

            Assert.Equal("ttf", await File.ReadAllTextAsync(Path.Combine(output, "font.ttf"), CancellationToken.None));
        }

        [Fact]
        public async Task ExtractAllAttachments_EntryReplaced_ReplacesTheCopiedFont()
        {
            // A font copied for an earlier transcode must not outlive a corrected entry, for example after an interrupted upload.
            var path = _fixture.CreateCompleteVideo();
            var mediaSource = CreatePlayedSource(path, false);
            var output = Path.Combine(_fixture.Root, "attachments-out");
            using var extractor = CreateAttachmentExtractor(mediaSource.Id, output);
            await extractor.ExtractAllAttachments(mediaSource.Path, mediaSource, CancellationToken.None);

            await File.WriteAllTextAsync(Path.Combine(TrueCacheLayout.GetEntryDirectory(path)!, "attachments", "font.ttf"), "ttf, complete", CancellationToken.None);
            Assert.True(_trueCache.Validate(path).IsValid);
            await extractor.ExtractAllAttachments(mediaSource.Path, mediaSource, CancellationToken.None);

            Assert.Equal("ttf, complete", await File.ReadAllTextAsync(Path.Combine(output, "font.ttf"), CancellationToken.None));
        }

        [Fact]
        public async Task ExtractAllAttachments_ConcurrentTranscodes_EachFindTheCompleteFont()
        {
            const int FontLength = 16 * 1024 * 1024;
            var path = _fixture.CreateCompleteVideo();

            // A large font keeps each copy running long enough for the transcodes to overlap.
            await File.WriteAllBytesAsync(Path.Combine(TrueCacheLayout.GetEntryDirectory(path)!, "attachments", "font.ttf"), new byte[FontLength], CancellationToken.None);
            var entry = _trueCache.Lookup(path).Entry!;
            var mediaSource = new MediaSourceInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Path = path,
                Protocol = MediaProtocol.File,
                MediaStreams = entry.CreateMediaInfo(true).MediaStreams,
                MediaAttachments = entry.MediaInfo!.MediaAttachments
            };
            var output = Path.Combine(_fixture.Root, "attachments-out");
            using var extractor = CreateAttachmentExtractor(mediaSource.Id, output);

            // Every transcode starts ffmpeg as soon as the call returns, so the font must be complete by then.
            var lengths = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                await extractor.ExtractAllAttachments(path, mediaSource, CancellationToken.None);
                return new FileInfo(Path.Combine(output, "font.ttf")).Length;
            })));

            Assert.All(lengths, length => Assert.Equal(FontLength, length));
        }

        /// <summary>
        /// Creates the media source playback uses: a .strm plays the target it names and a symlinked file plays the link target.
        /// </summary>
        private MediaSourceInfo CreatePlayedSource(string path, bool shortcut)
        {
            var entry = _trueCache.Lookup(path).Entry!;
            return new MediaSourceInfo
            {
                Id = Guid.NewGuid().ToString("N"),
                Path = shortcut ? "https://example.com/Film.mkv" : Path.Combine(_fixture.Root, "target", "Film.mkv"),
                ItemPath = path,
                Protocol = shortcut ? MediaProtocol.Http : MediaProtocol.File,
                MediaStreams = entry.CreateMediaInfo(true).MediaStreams,
                MediaAttachments = entry.MediaInfo!.MediaAttachments
            };
        }

        private SubtitleEncoder CreateSubtitleEncoder() => new(
            NullLogger<SubtitleEncoder>.Instance,
            Mock.Of<IFileSystem>(),
            Mock.Of<IMediaEncoder>(MockBehavior.Strict),
            Mock.Of<IHttpClientFactory>(MockBehavior.Strict),
            Mock.Of<IMediaSourceManager>(MockBehavior.Strict),
            Mock.Of<ISubtitleParser>(MockBehavior.Strict),
            Mock.Of<IPathManager>(MockBehavior.Strict),
            Mock.Of<IServerConfigurationManager>(),
            Mock.Of<ILocalizationManager>(),
            _trueCache);

        private AttachmentExtractor CreateAttachmentExtractor(string mediaSourceId, string outputFolder)
        {
            var pathManager = new Mock<IPathManager>(MockBehavior.Strict);
            pathManager.Setup(p => p.GetAttachmentFolderPath(mediaSourceId)).Returns(outputFolder);
            return new AttachmentExtractor(
                NullLogger<AttachmentExtractor>.Instance,
                Mock.Of<IFileSystem>(),
                Mock.Of<IMediaEncoder>(MockBehavior.Strict),
                Mock.Of<IMediaSourceManager>(MockBehavior.Strict),
                pathManager.Object,
                _trueCache);
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
