using System.IO;
using MediaBrowser.Controller.TrueCache;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.TrueCache
{
    public class TrueCacheLayoutTests
    {
        private static readonly string _root = Path.Combine(Path.GetTempPath(), "media");

        [Fact]
        public void GetEntryDirectory_File_IsSiblingCacheFolder()
        {
            Assert.Equal(
                Path.Combine(_root, "__true_cache__", "Film.mkv"),
                TrueCacheLayout.GetEntryDirectory(Path.Combine(_root, "Film.mkv")));
        }

        [Fact]
        public void GetEntryDirectory_FolderWithTrailingSeparator_UsesFolderName()
        {
            Assert.Equal(
                Path.Combine(_root, "__true_cache__", "Disc"),
                TrueCacheLayout.GetEntryDirectory(Path.Combine(_root, "Disc") + Path.DirectorySeparatorChar));
        }

        [Fact]
        public void GetEntryDirectory_CacheFolderItself_IsNull()
        {
            Assert.Null(TrueCacheLayout.GetEntryDirectory(Path.Combine(_root, "__true_cache__")));
        }

        [Fact]
        public void TryGetResourceOfCachePath_FileInEntry_ReturnsResource()
        {
            var path = Path.Combine(_root, "__true_cache__", "Film.mkv", "chapters", "0.jpg");

            Assert.True(TrueCacheLayout.TryGetResourceOfCachePath(path, out var resource));
            Assert.Equal(Path.Combine(_root, "Film.mkv"), resource);
        }

        [Fact]
        public void TryGetResourceOfCachePath_AlbumEntry_HasNoResource()
        {
            var path = Path.Combine(_root, "__true_cache__", "__album__", "meta.toml");

            Assert.True(TrueCacheLayout.TryGetResourceOfCachePath(path, out var resource));
            Assert.Null(resource);
        }

        [Theory]
        [InlineData(320, 1920, 320)]
        [InlineData(321, 1920, 320)]
        [InlineData(320, 241, 240)]
        [InlineData(320, null, 320)]
        public void GetTrickplayWidth_NeverExceedsVideoAndIsEven(int configured, int? video, int expected)
        {
            Assert.Equal(expected, TrueCacheRules.GetTrickplayWidth(configured, video));
        }

        [Fact]
        public void TryGetResourceOfCachePath_MediaFile_IsFalse()
        {
            Assert.False(TrueCacheLayout.TryGetResourceOfCachePath(Path.Combine(_root, "Film.mkv"), out _));
        }
    }
}
