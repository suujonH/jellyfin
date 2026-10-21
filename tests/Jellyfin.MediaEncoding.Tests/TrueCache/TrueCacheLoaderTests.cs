using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.MediaEncoding.TrueCache;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Tests.TrueCache
{
    public sealed class TrueCacheLoaderTests : System.IDisposable
    {
        private readonly TrueCacheFixture _fixture = new();
        private readonly TrueCacheLoader _loader = new(NullLogger.Instance, Mock.Of<ILocalizationManager>());

        public void Dispose() => _fixture.Dispose();

        [Fact]
        public void Load_WithoutEntry_ReturnsNone()
        {
            var path = _fixture.CreateResource("Film.mkv");

            Assert.Equal(TrueCacheState.None, _loader.Load(path, TrueCacheFixture.Requirements).State);
        }

        [Fact]
        public void Load_CompleteVideo_ReturnsEverythingDeclared()
        {
            var path = _fixture.CreateCompleteVideo();

            var result = _loader.Load(path, TrueCacheFixture.Requirements);

            Assert.True(result.IsValid, result.Reason);
            var mediaInfo = result.Entry.MediaInfo!;
            Assert.Equal("mkv", mediaInfo.Container);
            Assert.Equal(3, mediaInfo.MediaStreams.Count);
            Assert.Single(mediaInfo.MediaAttachments);
            Assert.Equal(2, mediaInfo.Chapters.Length);
            Assert.True(result.Entry.TryGetSubtitle(2, out var subtitle));
            Assert.EndsWith(Path.Combine("subtitles", "2.ass"), subtitle, System.StringComparison.Ordinal);
            Assert.Equal(new long[] { 0, 20000000, 40000000 }, result.Entry.Keyframes!.KeyframeTicks);
            Assert.Equal(320, Assert.Single(result.Entry.Manifest.Video!.Trickplay).Width);
        }

        [Theory]
        [InlineData(TrueCacheLayout.ProbeFile)]
        [InlineData(TrueCacheLayout.ScreenGrabFile)]
        [InlineData("chapters/500000000.jpg")]
        [InlineData("trickplay/320 - 10x10/0.jpg")]
        [InlineData("subtitles/2.ass")]
        [InlineData("attachments/font.ttf")]
        [InlineData(TrueCacheLayout.KeyframesFile)]
        public void Load_VideoMissingAFile_IsRejected(string missing)
        {
            var path = _fixture.CreateResource("Film.mkv");
            var files = TrueCacheFixture.CompleteVideoFiles();
            files.Remove(missing);
            _fixture.WriteEntry(path, files);

            var result = _loader.Load(path, TrueCacheFixture.Requirements);

            Assert.Equal(TrueCacheState.Rejected, result.State);
            Assert.Contains(missing, result.Reason, System.StringComparison.Ordinal);
        }

        [Fact]
        public void Load_TrickplayWidthNotDeclared_IsRejected()
        {
            var path = _fixture.CreateCompleteVideo();

            var result = _loader.Load(path, TrueCacheFixture.Requirements with { TrickplayWidths = [320, 640] });

            Assert.Equal(TrueCacheState.Rejected, result.State);
            Assert.Contains("width 640", result.Reason, System.StringComparison.Ordinal);
        }

        [Fact]
        public void Load_VideoShorterThanTrickplayInterval_NeedsNoTrickplay()
        {
            var path = _fixture.CreateResource("Film.mkv");
            var files = TrueCacheFixture.CompleteVideoFiles();
            files.Remove("trickplay/320 - 10x10/0.jpg");
            files[TrueCacheLayout.MetaFile] = "version = 1\nkind = \"video\"\n[source]\nsize = 4\n";
            _fixture.WriteEntry(path, files);

            var result = _loader.Load(path, TrueCacheFixture.Requirements with { TrickplayInterval = 200000 });

            Assert.True(result.IsValid, result.Reason);
        }

        [Fact]
        public void Load_DummyChapters_NeedImages()
        {
            var path = _fixture.CreateResource("Film.mkv");
            var files = TrueCacheFixture.CompleteVideoFiles();
            files[TrueCacheLayout.ProbeFile] = TrueCacheFixture.VideoProbe.Replace("\"chapters\"", "\"unused\"", System.StringComparison.Ordinal);
            _fixture.WriteEntry(path, files);

            // 100 seconds in 30 second chapters: 0 s, 30 s and 60 s; the image at 0 s is already there.
            var result = _loader.Load(path, TrueCacheFixture.Requirements with { DummyChapterDuration = 30 });

            Assert.Equal(TrueCacheState.Rejected, result.State);
            Assert.Contains("chapters/300000000.jpg", result.Reason, System.StringComparison.Ordinal);
            Assert.Contains("chapters/600000000.jpg", result.Reason, System.StringComparison.Ordinal);
        }

        [Fact]
        public void Load_SizeDiffersFromResource_IsRejected()
        {
            var path = _fixture.CreateResource("Film.mkv");
            var files = TrueCacheFixture.CompleteVideoFiles();
            files[TrueCacheLayout.MetaFile] = TrueCacheFixture.VideoMeta.Replace("size = 4", "size = 5", System.StringComparison.Ordinal);
            _fixture.WriteEntry(path, files);

            var result = _loader.Load(path, TrueCacheFixture.Requirements);

            Assert.Equal(TrueCacheState.Rejected, result.State);
            Assert.Contains("source.size", result.Reason, System.StringComparison.Ordinal);
        }

        [Fact]
        public void Load_UnknownKey_IsRejected()
        {
            var path = _fixture.CreateResource("Film.mkv");
            var files = TrueCacheFixture.CompleteVideoFiles();
            files[TrueCacheLayout.MetaFile] = TrueCacheFixture.VideoMeta + "\n[video]\ntimestap = \"valid\"\n";
            _fixture.WriteEntry(path, files);

            var result = _loader.Load(path, TrueCacheFixture.Requirements);

            Assert.Equal(TrueCacheState.Rejected, result.State);
        }

        [Theory]
        [InlineData("")]
        [InlineData("kind = \"video\"\n[source]\nsize = 4\n")]
        public void Load_MetaWithoutVersion_IsRejected(string meta)
        {
            var path = _fixture.CreateResource("Film.mkv");
            var files = TrueCacheFixture.CompleteVideoFiles();
            files[TrueCacheLayout.MetaFile] = meta;
            _fixture.WriteEntry(path, files);

            var result = _loader.Load(path, TrueCacheFixture.Requirements);

            Assert.Equal(TrueCacheState.Rejected, result.State);
            Assert.Contains("version", result.Reason, System.StringComparison.Ordinal);
        }

        [Fact]
        public void Load_StagedEntry_IsValidatedWhereItIs()
        {
            // The generator validates an entry in its staging folder before it moves it into place.
            var path = _fixture.CreateResource("Film.mkv");
            var staging = Path.Combine(_fixture.Root, TrueCacheLayout.FolderName, ".Film.mkv.staging");
            foreach (var (name, content) in TrueCacheFixture.CompleteVideoFiles())
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(staging, name))!);
                File.WriteAllText(Path.Combine(staging, name), content);
            }

            Assert.True(_loader.Load(path, staging, TrueCacheFixture.Requirements).IsValid);
            Assert.Equal(TrueCacheState.None, _loader.Load(path, TrueCacheFixture.Requirements).State);
        }

        [Fact]
        public void Load_SameEntryWithOtherTimestamps_HasTheSameHash()
        {
            // A copy that keeps or resets the modification times is still the same entry.
            var path = _fixture.CreateCompleteVideo();
            var directory = TrueCacheLayout.GetEntryDirectory(path)!;
            var before = _loader.Load(path, TrueCacheFixture.Requirements).Entry!.Hash;

            var longAgo = new System.DateTime(2020, 1, 1, 0, 0, 0, System.DateTimeKind.Utc);
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                File.SetLastWriteTimeUtc(file, longAgo);
            }

            Directory.SetLastWriteTimeUtc(directory, longAgo);

            Assert.Equal(before, _loader.Load(path, TrueCacheFixture.Requirements).Entry!.Hash);
        }

        [Theory]
        [InlineData("chapters/0.jpg", "jpeg")]
        [InlineData("trickplay/320 - 10x10/1.jpg", "jpg")]
        public void Load_FileRewrittenOrAdded_ChangesTheHash(string name, string content)
        {
            // The server does not read images, so their names and sizes stand for them.
            var path = _fixture.CreateCompleteVideo();
            var before = _loader.Load(path, TrueCacheFixture.Requirements).Entry!.Hash;

            _fixture.WriteEntry(path, new Dictionary<string, string> { [name] = content });

            Assert.NotEqual(before, _loader.Load(path, TrueCacheFixture.Requirements).Entry!.Hash);
        }

        [Fact]
        public void Load_ProbeEditedToTheSameSize_ChangesTheHash()
        {
            // The server reads the probe result, so its contents count, not only its size.
            var path = _fixture.CreateCompleteVideo();
            var before = _loader.Load(path, TrueCacheFixture.Requirements).Entry!.Hash;

            _fixture.WriteEntry(path, new Dictionary<string, string>
            {
                [TrueCacheLayout.ProbeFile] = TrueCacheFixture.VideoProbe.Replace("\"width\": 1920", "\"width\": 1921", System.StringComparison.Ordinal)
            });

            var after = _loader.Load(path, TrueCacheFixture.Requirements);
            Assert.True(after.IsValid, after.Reason);
            Assert.NotEqual(before, after.Entry.Hash);
        }

        [Theory]
        [InlineData("Comic.cbz", "book/ComicInfo.xml", "<ComicInfo><Title>Teh</Title></ComicInfo>", "<ComicInfo><Title>The</Title></ComicInfo>")]
        [InlineData("Song.mp3", TrueCacheLayout.LyricsFile, "[00:01.00]Helo!", "[00:01.00]Hello")]
        public void Load_ImportedDocumentEditedToTheSameSize_ChangesTheHash(string name, string document, string original, string edited)
        {
            // The providers import these documents, so their contents count as well.
            var path = _fixture.CreateResource(name);
            var files = name.EndsWith(".cbz", System.StringComparison.Ordinal)
                ? new Dictionary<string, string>
                {
                    [TrueCacheLayout.MetaFile] = "version = 1\nkind = \"book\"\n[source]\nsize = 4\n[book]\npage_count = 24\ncomic_info = true\ncomic_book_info = false\ncover = true\n",
                    ["book/cover.png"] = "png"
                }
                : TrueCacheFixture.CompleteAudioFiles();
            files[document] = original;
            _fixture.WriteEntry(path, files);
            var before = _loader.Load(path, TrueCacheFixture.Requirements);
            Assert.True(before.IsValid, before.Reason);

            _fixture.WriteEntry(path, new Dictionary<string, string> { [document] = edited });

            Assert.NotEqual(before.Entry.Hash, _loader.Load(path, TrueCacheFixture.Requirements).Entry!.Hash);
        }

        [Fact]
        public void LoadAlbum_MetaWithoutVersion_IsRejected()
        {
            var albumEntry = TrueCacheLayout.GetAlbumEntryDirectory(_fixture.Root);
            Directory.CreateDirectory(albumEntry);
            File.WriteAllText(Path.Combine(albumEntry, TrueCacheLayout.MetaFile), "kind = \"album\"\n[album]\nlufs = -9.25\n");

            Assert.Equal(TrueCacheState.Rejected, _loader.LoadAlbum(_fixture.Root).State);
        }

        [Fact]
        public void Load_DiscKindForFile_IsRejected()
        {
            var path = _fixture.CreateResource("Film.mkv");
            var files = TrueCacheFixture.CompleteVideoFiles();
            files[TrueCacheLayout.MetaFile] = TrueCacheFixture.VideoMeta.Replace("kind = \"video\"", "kind = \"bluray\"", System.StringComparison.Ordinal);
            _fixture.WriteEntry(path, files);

            Assert.Equal(TrueCacheState.Rejected, _loader.Load(path, TrueCacheFixture.Requirements).State);
        }

        [Fact]
        public void Load_CompleteAudio_ReturnsTagsAndCover()
        {
            var path = _fixture.CreateResource("Song.mp3");
            _fixture.WriteEntry(path, TrueCacheFixture.CompleteAudioFiles());

            var result = _loader.Load(path, TrueCacheFixture.Requirements);

            Assert.True(result.IsValid, result.Reason);
            var audio = result.Entry.Manifest.Audio!;
            Assert.Equal(-14.5f, audio.Lufs);
            Assert.Equal(new[] { "A", "B" }, audio.Tags!.Artists);
            Assert.Equal("-6.5 dB", audio.Tags.AdditionalFields["REPLAYGAIN_TRACK_GAIN"]);
            Assert.True(result.Entry.TryGetEmbeddedImage(ImageType.Primary, out _));
        }

        [Theory]
        [InlineData("lufs = -14.5\n", "audio.lufs")]
        [InlineData("embedded_lyrics = true\n", "audio.embedded_lyrics")]
        public void Load_AudioMissingAValue_IsRejected(string line, string key)
        {
            var path = _fixture.CreateResource("Song.mp3");
            var files = TrueCacheFixture.CompleteAudioFiles();
            files[TrueCacheLayout.MetaFile] = TrueCacheFixture.AudioMeta.Replace(line, string.Empty, System.StringComparison.Ordinal);
            _fixture.WriteEntry(path, files);

            var result = _loader.Load(path, TrueCacheFixture.Requirements);

            Assert.Equal(TrueCacheState.Rejected, result.State);
            Assert.Contains(key, result.Reason, System.StringComparison.Ordinal);
        }

        [Fact]
        public void Load_ComicArchive_ReturnsDocumentsAndCover()
        {
            var path = _fixture.CreateResource("Comic.cbz");
            _fixture.WriteEntry(path, new System.Collections.Generic.Dictionary<string, string>
            {
                [TrueCacheLayout.MetaFile] = "version = 1\nkind = \"book\"\n[source]\nsize = 4\n[book]\npage_count = 24\ncomic_info = true\ncomic_book_info = false\ncover = true\n",
                ["book/ComicInfo.xml"] = "<ComicInfo />",
                ["book/cover.png"] = "png"
            });

            var result = _loader.Load(path, TrueCacheFixture.Requirements);

            Assert.True(result.IsValid, result.Reason);
            Assert.EndsWith("cover.png", result.Entry.GetBookCover(), System.StringComparison.Ordinal);
        }

        [Fact]
        public void Load_ComicArchiveWithoutDeclaredComicInfo_IsRejected()
        {
            var path = _fixture.CreateResource("Comic.cbz");
            _fixture.WriteEntry(path, new System.Collections.Generic.Dictionary<string, string>
            {
                [TrueCacheLayout.MetaFile] = "version = 1\nkind = \"book\"\n[source]\nsize = 4\n[book]\npage_count = 24\ncomic_book_info = false\ncover = false\n"
            });

            var result = _loader.Load(path, TrueCacheFixture.Requirements);

            Assert.Equal(TrueCacheState.Rejected, result.State);
            Assert.Contains("book.comic_info", result.Reason, System.StringComparison.Ordinal);
        }

        [Fact]
        public void LoadAlbum_ReadsAlbumLoudness()
        {
            var albumEntry = TrueCacheLayout.GetAlbumEntryDirectory(_fixture.Root);
            Directory.CreateDirectory(albumEntry);
            File.WriteAllText(Path.Combine(albumEntry, TrueCacheLayout.MetaFile), "version = 1\nkind = \"album\"\n[album]\nlufs = -9.25\n");

            var result = _loader.LoadAlbum(_fixture.Root);

            Assert.True(result.IsValid, result.Reason);
            Assert.Equal(-9.25f, result.Entry.Manifest.AlbumLufs);
        }

        [Fact]
        public void CreateMediaInfo_ReturnsIndependentCopies()
        {
            var path = _fixture.CreateCompleteVideo();
            var entry = _loader.Load(path, TrueCacheFixture.Requirements).Entry!;

            var first = entry.CreateMediaInfo(true);
            first.MediaStreams[0].Index = 42;
            var second = entry.CreateMediaInfo(false);

            Assert.Equal(0, second.MediaStreams[0].Index);
            Assert.Empty(second.Chapters);
            Assert.Equal(2, entry.CreateMediaInfo(true).Chapters.Length);
        }

        [Fact]
        public void FindProbeSubtitleIndex_MatchesStreamsInOrder()
        {
            var path = _fixture.CreateCompleteVideo();
            var entry = _loader.Load(path, TrueCacheFixture.Requirements).Entry!;

            // The server renumbers streams; an external audio stream in front moves the subtitle to index 3.
            var sourceStreams = entry.CreateMediaInfo(true).MediaStreams.ToList();
            sourceStreams.Insert(0, new MediaStream { Type = MediaStreamType.Audio, IsExternal = true });
            for (var i = 0; i < sourceStreams.Count; i++)
            {
                sourceStreams[i].Index = i;
            }

            var subtitle = sourceStreams.Single(s => s.Type == MediaStreamType.Subtitle);

            Assert.Equal(3, subtitle.Index);
            Assert.Equal(2, entry.FindProbeSubtitleIndex(sourceStreams, subtitle, Mock.Of<ILocalizationManager>()));
        }

        [Fact]
        public void FindProbeSubtitleIndex_LanguageAsTheLibraryReturnsIt_Matches()
        {
            // The probe keeps the ISO 639-2/B code it read; the library returns stored streams with the /T code.
            var path = _fixture.CreateResource("Film.mkv");
            var files = TrueCacheFixture.CompleteVideoFiles();
            files[TrueCacheLayout.ProbeFile] = TrueCacheFixture.VideoProbe.Replace("\"jpn\"", "\"ger\"", System.StringComparison.Ordinal);
            _fixture.WriteEntry(path, files);
            var entry = _loader.Load(path, TrueCacheFixture.Requirements).Entry!;

            var sourceStreams = entry.CreateMediaInfo(true).MediaStreams;
            var subtitle = sourceStreams.Single(s => s.Type == MediaStreamType.Subtitle);
            Assert.Equal("ger", subtitle.Language);
            subtitle.Language = "deu";

            Assert.Equal(2, entry.FindProbeSubtitleIndex(sourceStreams, subtitle, CreateLocalization()));
        }

        [Fact]
        public void FindProbeSubtitleIndex_UnmatchedStream_LeavesLaterStreamsMatchable()
        {
            var path = _fixture.CreateResource("Film.mkv");
            var files = TrueCacheFixture.CompleteVideoFiles();
            files[TrueCacheLayout.ProbeFile] = TrueCacheFixture.VideoProbe.Replace(
                "{ \"index\": 3, \"codec_name\": \"ttf\"",
                "{ \"index\": 4, \"codec_name\": \"ass\", \"codec_type\": \"subtitle\", \"disposition\": { \"default\": 0 }, \"tags\": { \"language\": \"eng\", \"title\": \"Full\" } },\n    { \"index\": 3, \"codec_name\": \"ttf\"",
                System.StringComparison.Ordinal);
            files["subtitles/4.ass"] = "[Script Info]";
            _fixture.WriteEntry(path, files);
            var entry = _loader.Load(path, TrueCacheFixture.Requirements).Entry!;

            var sourceStreams = entry.CreateMediaInfo(true).MediaStreams;
            var subtitles = sourceStreams.Where(s => s.Type == MediaStreamType.Subtitle).ToList();
            Assert.Equal(2, subtitles.Count);
            subtitles[0].Title = "Renamed";

            Assert.Null(entry.FindProbeSubtitleIndex(sourceStreams, subtitles[0], CreateLocalization()));
            Assert.Equal(4, entry.FindProbeSubtitleIndex(sourceStreams, subtitles[1], CreateLocalization()));
        }

        private static ILocalizationManager CreateLocalization()
        {
            var localization = new Mock<ILocalizationManager>();
            var isoT = "deu";
            localization.Setup(l => l.TryGetISO6392TFromB("ger", out isoT)).Returns(true);
            return localization.Object;
        }
    }
}
