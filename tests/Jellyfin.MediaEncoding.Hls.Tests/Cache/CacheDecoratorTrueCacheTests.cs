using System;
using System.Collections.Generic;
using System.Threading;
using Jellyfin.MediaEncoding.Hls.Cache;
using Jellyfin.MediaEncoding.Hls.Extractors;
using Jellyfin.MediaEncoding.Keyframes;
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
        var entry = new TrueCacheEntry(FilePath, "/media/__true_cache__/Film.mkv", new TrueCacheManifest(), null, null, keyframes, new Dictionary<ImageType, string>(), new Dictionary<int, string>());
        var (decorator, repository) = Create(TrueCacheLookup.Valid(entry));

        Assert.True(decorator.TryExtractKeyframes(Guid.NewGuid(), FilePath, out var result));
        Assert.Same(keyframes, result);
        repository.Verify(r => r.SaveKeyframeDataAsync(It.IsAny<Guid>(), keyframes, It.IsAny<CancellationToken>()), Times.Once());
    }

    [Fact]
    public void TryExtractKeyframes_RejectedVideo_IsNotRead()
    {
        var (decorator, _) = Create(TrueCacheLookup.Rejected("missing keyframes.json"));

        Assert.False(decorator.TryExtractKeyframes(Guid.NewGuid(), FilePath, out _));
    }

    // The strict extractor fails the test if the decorator ever asks it to read the file.
    private static (CacheDecorator Decorator, Mock<IKeyframeRepository> Repository) Create(TrueCacheLookup lookup)
    {
        var repository = new Mock<IKeyframeRepository>();
        repository.Setup(r => r.GetKeyframeData(It.IsAny<Guid>())).Returns([]);
        var extractor = new Mock<IKeyframeExtractor>(MockBehavior.Strict);
        var trueCache = new Mock<ITrueCacheService>();
        trueCache.Setup(t => t.Lookup(FilePath)).Returns(lookup);
        return (new CacheDecorator(repository.Object, extractor.Object, NullLogger<CacheDecorator>.Instance, trueCache.Object), repository);
    }
}
