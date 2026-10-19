using System;
using System.IO;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
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
        public void Lookup_EntryStillStaged_IsNone()
        {
            // A partly written entry in the generator's staging folder must not get the resource rejected.
            var path = _fixture.CreateResource("Film.mkv");
            var staging = Path.Combine(_fixture.Root, TrueCacheLayout.FolderName, ".Film.mkv.staging");
            Directory.CreateDirectory(staging);
            File.WriteAllText(Path.Combine(staging, TrueCacheLayout.ProbeFile), TrueCacheFixture.VideoProbe);

            Assert.Equal(TrueCacheState.None, _service.Lookup(path).State);
            Assert.Empty(_service.GetRejections());
        }

        [Fact]
        public void Lookup_RelativePath_IsNone()
        {
            Assert.Equal(TrueCacheState.None, _service.Lookup("Film.mkv").State);
        }

        [Fact]
        public void HasDeclarationChanged_ComparesMetaWithLastRefresh()
        {
            var path = _fixture.CreateCompleteVideo();
            var written = File.GetLastWriteTimeUtc(Path.Combine(TrueCacheLayout.GetEntryDirectory(path)!, TrueCacheLayout.MetaFile));

            Assert.True(_service.HasDeclarationChanged(new Video { Path = path, DateLastRefreshed = written.AddMinutes(-1) }));
            Assert.False(_service.HasDeclarationChanged(new Video { Path = path, DateLastRefreshed = written.AddMinutes(1) }));
        }

        [Fact]
        public void HasDeclarationChanged_ValidEntryRemoved_IsTrueUntilTheNextRefresh()
        {
            // A removed entry leaves nothing on disk, yet the item still points at the chapter images and trickplay it provided.
            var path = _fixture.CreateCompleteVideo();
            var refreshed = DateTime.UtcNow.AddMinutes(-1);
            Assert.True(_service.Lookup(path).IsValid);

            Directory.Delete(TrueCacheLayout.GetEntryDirectory(path)!, true);

            Assert.True(_service.HasDeclarationChanged(new Video { Path = path, DateLastRefreshed = refreshed }));
            Assert.False(_service.HasDeclarationChanged(new Video { Path = path, DateLastRefreshed = DateTime.UtcNow.AddMinutes(1) }));
        }

        [Fact]
        public void HasDeclarationChanged_RejectedEntryRemoved_IsFalse()
        {
            // Nothing of a rejected entry was ever applied to the item.
            var path = _fixture.CreateResource("Film.mkv");
            var files = TrueCacheFixture.CompleteVideoFiles();
            files.Remove(TrueCacheLayout.ProbeFile);
            var directory = _fixture.WriteEntry(path, files);
            Assert.Equal(TrueCacheState.Rejected, _service.Lookup(path).State);

            Directory.Delete(directory, true);

            Assert.False(_service.HasDeclarationChanged(new Video { Path = path, DateLastRefreshed = DateTime.MinValue }));
        }

        [Fact]
        public void HasDeclarationChanged_RejectedEntry_IsFalse()
        {
            var path = _fixture.CreateResource("Film.mkv");
            var files = TrueCacheFixture.CompleteVideoFiles();
            files.Remove(TrueCacheLayout.ProbeFile);
            _fixture.WriteEntry(path, files);

            Assert.False(_service.HasDeclarationChanged(new Video { Path = path, DateLastRefreshed = DateTime.MinValue }));
        }
    }
}
