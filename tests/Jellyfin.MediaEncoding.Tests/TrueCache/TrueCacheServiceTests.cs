using System;
using System.IO;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.MediaEncoding.TrueCache;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.TrueCache
{
    public sealed class TrueCacheServiceTests : IDisposable
    {
        private readonly TrueCacheFixture _fixture = new();
        private readonly TrueCacheService _service;

        public TrueCacheServiceTests()
        {
            var config = new Mock<IServerConfigurationManager>();
            config.SetupGet(c => c.Configuration).Returns(new ServerConfiguration { TrickplayOptions = new TrickplayOptions { WidthResolutions = [320] } });
            _service = new TrueCacheService(NullLogger<TrueCacheService>.Instance, Mock.Of<ILocalizationManager>(), config.Object);
        }

        public void Dispose() => _fixture.Dispose();

        [Fact]
        public void Lookup_CompleteEntry_IsValid()
        {
            var path = _fixture.CreateCompleteVideo();

            Assert.True(_service.Lookup(path).IsValid);
            Assert.Empty(_service.GetRejections());
        }

        [Fact]
        public void Lookup_IncompleteEntry_IsListedAsRejected()
        {
            var path = _fixture.CreateResource("Film.mkv");
            var files = TrueCacheFixture.CompleteVideoFiles();
            files.Remove(TrueCacheLayout.KeyframesFile);
            _fixture.WriteEntry(path, files);

            var result = _service.Lookup(path);

            Assert.Equal(TrueCacheState.Rejected, result.State);
            var rejection = Assert.Single(_service.GetRejections());
            Assert.Equal(path, rejection.ResourcePath);
        }

        [Fact]
        public void Validate_AfterCompletingEntry_ClearsRejection()
        {
            var path = _fixture.CreateResource("Film.mkv");
            var files = TrueCacheFixture.CompleteVideoFiles();
            files.Remove(TrueCacheLayout.KeyframesFile);
            var directory = _fixture.WriteEntry(path, files);
            Assert.Equal(TrueCacheState.Rejected, _service.Lookup(path).State);

            File.WriteAllText(Path.Combine(directory, TrueCacheLayout.KeyframesFile), """{ "TotalDuration": 1, "KeyframeTicks": [0] }""");

            Assert.True(_service.Validate(path).IsValid);
            Assert.Empty(_service.GetRejections());
        }

        [Fact]
        public void Lookup_ResourceWithoutEntryNextToDeclaredOne_IsNone()
        {
            _fixture.CreateCompleteVideo();
            var other = _fixture.CreateResource("Other.mkv");

            Assert.Equal(TrueCacheState.None, _service.Lookup(other).State);
        }

        [Fact]
        public void Lookup_RelativePath_IsNone()
        {
            Assert.Equal(TrueCacheState.None, _service.Lookup("Film.mkv").State);
        }
    }
}
