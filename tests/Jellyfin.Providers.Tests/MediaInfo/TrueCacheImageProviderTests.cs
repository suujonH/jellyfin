using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using MediaBrowser.Providers.Books;
using MediaBrowser.Providers.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Providers.Tests.MediaInfo
{
    /// <summary>
    /// Image providers hand out the images of a declared resource without asking the encoder, which is strict here.
    /// </summary>
    public sealed class TrueCacheImageProviderTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "jellyfin-truecache-images-" + Guid.NewGuid().ToString("N"));

        public TrueCacheImageProviderTests()
        {
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            Directory.Delete(_root, true);
        }

        [Fact]
        public async Task VideoImageProvider_DeclaredVideo_ReturnsScreenGrab()
        {
            var (movie, entry) = CreateEntry("Film.mkv", new TrueCacheManifest { Kind = TrueCacheKind.Video }, new Dictionary<ImageType, string>());
            await File.WriteAllTextAsync(entry.ScreenGrabPath, "jpg", TestContext.Current.CancellationToken);
            var provider = new VideoImageProvider(Mock.Of<IMediaSourceManager>(MockBehavior.Strict), Mock.Of<IMediaEncoder>(MockBehavior.Strict), NullLogger<VideoImageProvider>.Instance, Valid(entry));

            var response = await provider.GetImage(movie, ImageType.Primary, CancellationToken.None);

            Assert.True(response.HasImage);
            Assert.Null(response.Path);
            await using (response.Stream)
            {
                Assert.NotNull(response.Stream);
            }
        }

        [Fact]
        public async Task VideoImageProvider_RejectedVideo_ReturnsNoImage()
        {
            var movie = new Movie { Path = Path.Combine(_root, "Film.mkv"), DefaultVideoStreamIndex = 0 };
            var trueCache = new Mock<ITrueCacheService>();
            trueCache.Setup(t => t.Lookup(movie.Path)).Returns(TrueCacheLookup.Rejected("missing probe.json"));
            var provider = new VideoImageProvider(Mock.Of<IMediaSourceManager>(MockBehavior.Strict), Mock.Of<IMediaEncoder>(MockBehavior.Strict), NullLogger<VideoImageProvider>.Instance, trueCache.Object);

            var response = await provider.GetImage(movie, ImageType.Primary, CancellationToken.None);

            Assert.False(response.HasImage);
        }

        [Theory]
        [InlineData(ImageType.Primary, true)]
        [InlineData(ImageType.Backdrop, false)]
        public async Task EmbeddedImageProvider_DeclaredVideo_ReturnsDeclaredImages(ImageType type, bool hasImage)
        {
            var primary = Path.Combine(_root, "poster.png");
            await File.WriteAllTextAsync(primary, "png", TestContext.Current.CancellationToken);
            var (movie, entry) = CreateEntry("Film.mkv", new TrueCacheManifest { Kind = TrueCacheKind.Video }, new Dictionary<ImageType, string> { [ImageType.Primary] = primary });
            var provider = new EmbeddedImageProvider(Mock.Of<IMediaSourceManager>(MockBehavior.Strict), Mock.Of<IMediaEncoder>(MockBehavior.Strict), NullLogger<EmbeddedImageProvider>.Instance, Valid(entry));

            var response = await provider.GetImage(movie, type, CancellationToken.None);

            Assert.Equal(hasImage, response.HasImage);
            if (hasImage)
            {
                Assert.Equal(ImageFormat.Png, response.Format);
                await response.Stream.DisposeAsync();
            }
        }

        [Fact]
        public async Task AudioImageProvider_DeclaredAudio_ReturnsDeclaredCover()
        {
            var cover = Path.Combine(_root, "cover.jpg");
            await File.WriteAllTextAsync(cover, "jpg", TestContext.Current.CancellationToken);
            var path = Path.Combine(_root, "Song.mp3");
            var entry = new TrueCacheEntry(path, _root, new TrueCacheManifest { Kind = TrueCacheKind.Audio }, null, null, null, new Dictionary<ImageType, string> { [ImageType.Primary] = cover }, new Dictionary<int, string>());
            var provider = new AudioImageProvider(Mock.Of<IMediaSourceManager>(MockBehavior.Strict), Mock.Of<IMediaEncoder>(MockBehavior.Strict), Mock.Of<IServerConfigurationManager>(), Mock.Of<IFileSystem>(), Valid(entry));

            var response = await provider.GetImage(new Audio { Path = path }, ImageType.Primary, CancellationToken.None);

            Assert.True(response.HasImage);
            Assert.Equal(ImageFormat.Jpg, response.Format);
            await response.Stream.DisposeAsync();
        }

        [Fact]
        public async Task ComicImageProvider_DeclaredArchive_ReturnsDeclaredCover()
        {
            var path = Path.Combine(_root, "Comic.cbz");
            var entryDirectory = TrueCacheLayout.GetEntryDirectory(path)!;
            Directory.CreateDirectory(Path.Combine(entryDirectory, TrueCacheLayout.BookFolder));
            await File.WriteAllTextAsync(Path.Combine(entryDirectory, TrueCacheLayout.BookFolder, "cover.webp"), "webp", TestContext.Current.CancellationToken);
            var entry = new TrueCacheEntry(path, entryDirectory, new TrueCacheManifest { Kind = TrueCacheKind.Book, Book = new TrueCacheBookInfo { Cover = true } }, null, null, null, new Dictionary<ImageType, string>(), new Dictionary<int, string>());
            var provider = new ComicImageProvider(NullLogger<ComicImageProvider>.Instance, Valid(entry));

            var response = await provider.GetImage(new Book { Path = path }, ImageType.Primary, CancellationToken.None);

            Assert.True(response.HasImage);
            Assert.Equal(ImageFormat.Webp, response.Format);
            await response.Stream.DisposeAsync();
        }

        private static ITrueCacheService Valid(TrueCacheEntry entry)
        {
            var trueCache = new Mock<ITrueCacheService>();
            trueCache.Setup(t => t.Lookup(entry.ResourcePath)).Returns(TrueCacheLookup.Valid(entry));
            return trueCache.Object;
        }

        private (Movie Movie, TrueCacheEntry Entry) CreateEntry(string name, TrueCacheManifest manifest, Dictionary<ImageType, string> images)
        {
            var path = Path.Combine(_root, name);
            var directory = TrueCacheLayout.GetEntryDirectory(path)!;
            Directory.CreateDirectory(directory);
            var movie = new Movie { Path = path, DefaultVideoStreamIndex = 0 };
            return (movie, new TrueCacheEntry(path, directory, manifest, null, null, null, images, new Dictionary<int, string>()));
        }
    }
}
