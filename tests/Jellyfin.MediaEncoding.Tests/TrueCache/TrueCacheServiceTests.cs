using System;
using System.Collections.Generic;
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
            _service = CreateService();
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
        public void GetResourceSize_DeclaredStrm_IsTheSizeOfItsTarget()
        {
            // The .strm itself is a few bytes of text; its declared size is the size of the media it names, which the library records.
            var path = _fixture.CreateResource("Film.strm");
            var files = TrueCacheFixture.CompleteVideoFiles();
            files[TrueCacheLayout.MetaFile] = TrueCacheFixture.VideoMeta.Replace("size = 4", "size = 123456789", StringComparison.Ordinal);
            _fixture.WriteEntry(path, files);

            Assert.Equal(123456789, _service.GetResourceSize(path, 4));
        }

        [Fact]
        public void GetResourceSize_WithoutEntry_IsTheFileLength()
        {
            var path = _fixture.CreateResource("Film.strm");

            Assert.Equal(4, _service.GetResourceSize(path, 4));
        }

        [Fact]
        public void HasDeclarationChanged_ItemRefreshedWithTheEntry_IsFalseAfterARestart()
        {
            // An entry that is already there when the server starts is not new: a restart must not refresh every declared item.
            var path = _fixture.CreateCompleteVideo();
            var item = new Video { Path = path, TrueCacheHash = _service.Lookup(path).Entry!.Hash };

            Assert.False(_service.HasDeclarationChanged(item));
            Assert.False(CreateService().HasDeclarationChanged(item));
        }

        [Fact]
        public void HasDeclarationChanged_NeitherEntryNorHash_IsFalse()
        {
            var path = _fixture.CreateResource("Film.mkv");

            Assert.False(_service.HasDeclarationChanged(new Video { Path = path }));
        }

        [Fact]
        public void HasDeclarationChanged_EntryAddedWithOldTimestamps_IsTrue()
        {
            // Copies that keep timestamps (rsync, rclone) leave the entry older than the item's last refresh,
            // and an entry added while the server was not running was never seen appearing.
            var path = _fixture.CreateResource("Film.mkv");
            var item = new Video { Path = path, DateLastRefreshed = DateTime.UtcNow };

            WriteEntryWrittenLongAgo(path, TrueCacheFixture.CompleteVideoFiles());

            Assert.True(CreateService().HasDeclarationChanged(item));
        }

        [Fact]
        public void HasDeclarationChanged_EntryRemoved_IsTrue()
        {
            // The item still holds what the removed entry provided, such as chapter images and trickplay tiles.
            var path = _fixture.CreateCompleteVideo();
            var item = new Video { Path = path, TrueCacheHash = _service.Lookup(path).Entry!.Hash };

            Directory.Delete(TrueCacheLayout.GetEntryDirectory(path)!, true);

            Assert.True(_service.HasDeclarationChanged(item));
            Assert.True(CreateService().HasDeclarationChanged(item));
        }

        [Fact]
        public void HasDeclarationChanged_EntryReplacedWithOldTimestamps_IsTrue()
        {
            // A regenerated entry copied over the old one keeps the times it was generated at, before the item's last refresh.
            var path = _fixture.CreateCompleteVideo();
            var item = new Video { Path = path, TrueCacheHash = _service.Lookup(path).Entry!.Hash, DateLastRefreshed = DateTime.UtcNow };
            var files = TrueCacheFixture.CompleteVideoFiles();
            files[TrueCacheLayout.ProbeFile] = TrueCacheFixture.VideoProbe.Replace("\"width\": 1920", "\"width\": 1921", StringComparison.Ordinal);

            WriteEntryWrittenLongAgo(path, files);

            Assert.True(_service.Lookup(path).IsValid);
            Assert.True(_service.HasDeclarationChanged(item));
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

            Assert.False(_service.HasDeclarationChanged(new Video { Path = path }));
        }

        [Fact]
        public void HasDeclarationChanged_RejectedEntry_IsFalse()
        {
            // The resource leaves the library instead.
            var path = _fixture.CreateCompleteVideo();
            var item = new Video { Path = path, TrueCacheHash = _service.Lookup(path).Entry!.Hash };
            File.Delete(Path.Combine(TrueCacheLayout.GetEntryDirectory(path)!, TrueCacheLayout.ProbeFile));

            Assert.Equal(TrueCacheState.Rejected, _service.Validate(path).State);
            Assert.False(_service.HasDeclarationChanged(item));
        }

        private static TrueCacheService CreateService()
        {
            var config = new Mock<IServerConfigurationManager>();
            config.SetupGet(c => c.Configuration).Returns(new ServerConfiguration { TrickplayOptions = new TrickplayOptions { WidthResolutions = [320] } });
            return new TrueCacheService(NullLogger<TrueCacheService>.Instance, Mock.Of<ILocalizationManager>(), config.Object);
        }

        private void WriteEntryWrittenLongAgo(string resourcePath, Dictionary<string, string> files)
        {
            var directory = _fixture.WriteEntry(resourcePath, files);
            var longAgo = DateTime.UtcNow.AddDays(-30);
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetLastWriteTimeUtc(file, longAgo);
            }

            Directory.SetLastWriteTimeUtc(directory, longAgo);
        }
    }
}
