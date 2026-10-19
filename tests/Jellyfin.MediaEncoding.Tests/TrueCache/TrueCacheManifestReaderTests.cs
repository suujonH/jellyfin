using System;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.MediaEncoding.TrueCache;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.MediaInfo;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.TrueCache
{
    public class TrueCacheManifestReaderTests
    {
        [Fact]
        public void Read_Video_ReadsAllSections()
        {
            var manifest = TrueCacheManifestReader.Read("""
                version = 1
                kind = "video"

                [source]
                size = 123
                modified = 2026-01-02T03:04:05Z

                [video]
                timestamp = "valid"

                [[video.trickplay]]
                width = 320
                height = 180
                tile_width = 10
                tile_height = 10
                thumbnail_count = 250
                interval = 10000
                bandwidth = 4000
                """);

            Assert.Equal(TrueCacheKind.Video, manifest.Kind);
            Assert.Equal(123, manifest.SourceSize);
            Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc), manifest.SourceModified);
            Assert.Equal(TransportStreamTimestamp.Valid, manifest.Video!.Timestamp);
            var trickplay = Assert.Single(manifest.Video.Trickplay);
            Assert.Equal(3, trickplay.TileCount);
            Assert.Equal("320 - 10x10", trickplay.FolderName);
        }

        [Fact]
        public void Read_Photo_ReadsTags()
        {
            var manifest = TrueCacheManifestReader.Read("""
                version = 1
                kind = "photo"

                [photo]
                width = 4000
                height = 3000
                camera_make = "Maker"
                orientation = "rightTop"
                keywords = ["a", "b"]
                aperture = 2
                """);

            var photo = manifest.Photo!;
            Assert.Equal(4000, photo.Width);
            Assert.Equal(ImageOrientation.RightTop, photo.Orientation);
            Assert.Equal(new[] { "a", "b" }, photo.Keywords);
            Assert.Equal(2d, photo.Aperture);
        }

        [Theory]
        [InlineData("version = 2\nkind = \"video\"")]
        [InlineData("kind = \"video\"")]
        [InlineData("version = 1\nkind = \"movie\"")]
        [InlineData("version = 1\nkind = \"video\"\nextra = 1")]
        [InlineData("version = 1\nkind = \"video\"\n[source]\nsize = \"4\"")]
        [InlineData("version = 1\nkind = \"video\"\n[video]\ntimestamp = \"often\"")]
        [InlineData("version = 1\nkind = ")]
        public void Read_InvalidDeclaration_Throws(string text)
        {
            Assert.ThrowsAny<Exception>(() => TrueCacheManifestReader.Read(text));
        }
    }
}
