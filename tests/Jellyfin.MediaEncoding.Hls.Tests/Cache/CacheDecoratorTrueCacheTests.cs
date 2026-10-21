using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.MediaEncoding.Hls.Cache;
using Jellyfin.MediaEncoding.Hls.Extractors;
using Jellyfin.MediaEncoding.Keyframes;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Cache;

public class CacheDecoratorTrueCacheTests
{
    private const string FilePath = "/media/Film.mkv";

    [Fact]
    public void TryExtractKeyframes_DeclaredVideo_UsesDeclaredKeyframes()
    {
        var keyframes = new KeyframeData(100, [0, 50]);
        var (decorator, repository) = Create(TrueCacheLookup.Valid(CreateEntry(keyframes)));

        Assert.True(decorator.TryExtractKeyframes(Guid.NewGuid(), FilePath, out var result));
        Assert.Same(keyframes, result);
        repository.Verify(r => r.SaveKeyframeDataAsync(It.IsAny<Guid>(), It.IsAny<KeyframeData>(), It.IsAny<CancellationToken>()), Times.Never());
    }

    [Fact]
    public void TryExtractKeyframes_DeclaredVideoWithPersistedKeyframes_UsesTheCurrentDeclaration()
    {
        // Keyframes persisted earlier, by an older entry or before the video was declared, must not outlive a replaced entry.
        var keyframes = new KeyframeData(100, [0, 50]);
        var (decorator, repository) = Create(TrueCacheLookup.Valid(CreateEntry(keyframes)));
        repository.Setup(r => r.GetKeyframeData(It.IsAny<Guid>())).Returns([new KeyframeData(100, [0, 40])]);

        Assert.True(decorator.TryExtractKeyframes(Guid.NewGuid(), FilePath, out var result));
        Assert.Same(keyframes, result);
    }

    [Fact]
    public void TryExtractKeyframes_RejectedVideo_IsNotRead()
    {
        var (decorator, _) = Create(TrueCacheLookup.Rejected("missing keyframes.json"));

        Assert.False(decorator.TryExtractKeyframes(Guid.NewGuid(), FilePath, out _));
    }

    [Fact]
    public void TryExtractKeyframes_VideoPlayedFromSymlinkTarget_UsesDeclaredKeyframes()
    {
        // Playback passes the file it reads; the entry sits next to the library item.
        var keyframes = new KeyframeData(100, [0, 50]);
        var item = new Video { Id = Guid.NewGuid(), Path = FilePath };
        var (decorator, _) = Create(TrueCacheLookup.Valid(CreateEntry(keyframes)), item);

        Assert.True(decorator.TryExtractKeyframes(item.Id, "/mnt/storage/Film.mkv", out var result));
        Assert.Same(keyframes, result);
    }

    private static TrueCacheEntry CreateEntry(KeyframeData keyframes)
        => new(FilePath, "/media/__true_cache__/Film.mkv", "hash", new TrueCacheManifest(), null, null, keyframes, new Dictionary<ImageType, string>(), new Dictionary<int, string>());

    // The strict extractor fails the test if the decorator ever asks it to read the file.
    private static (CacheDecorator Decorator, Mock<IKeyframeRepository> Repository) Create(TrueCacheLookup lookup, BaseItem? item = null)
    {
        var repository = new Mock<IKeyframeRepository>();
        repository.Setup(r => r.GetKeyframeData(It.IsAny<Guid>())).Returns([]);
        var extractor = new Mock<IKeyframeExtractor>(MockBehavior.Strict);
        var libraryManager = new Mock<ILibraryManager>();
        if (item is not null)
        {
            libraryManager.Setup(l => l.GetItemById(item.Id)).Returns(item);
        }

        var trueCache = new Mock<ITrueCacheService>();
        trueCache.Setup(t => t.Lookup(It.IsAny<string>())).Returns(TrueCacheLookup.None);
        trueCache.Setup(t => t.Lookup(FilePath)).Returns(lookup);
        return (new CacheDecorator(repository.Object, extractor.Object, NullLogger<CacheDecorator>.Instance, trueCache.Object, libraryManager.Object), repository);
    }
}
