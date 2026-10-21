using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Extractors;
using Jellyfin.MediaEncoding.Hls.Plans;
using Jellyfin.MediaEncoding.Keyframes;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.MediaEncoding.Hls.Tests.Plans;

public sealed class KeyframeSourceTests : IDisposable
{
    private static readonly KeyframeData _keyframes = new(100, [0, 50]);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "jellyfin-keyframe-source-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
    private readonly Mock<IKeyframeRepository> _repository = new();
    private readonly Mock<ITrueCacheService> _trueCache = new();
    private readonly EncodingOptions _options = new() { KeyframeExtractionTimeoutSeconds = 1 };

    public KeyframeSourceTests()
    {
        Directory.CreateDirectory(_directory);
        _repository.Setup(r => r.GetKeyframeData(It.IsAny<Guid>())).Returns([]);
        _trueCache.Setup(t => t.Lookup(It.IsAny<string>())).Returns(TrueCacheLookup.None);
    }

    private delegate bool TryExtract(Guid itemId, string filePath, out KeyframeData? keyframeData);

    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task GetKeyframesAsync_DeclaredVideo_UsesTheEntryWithoutReading()
    {
        var path = CreateFile("Film.mkv");
        _trueCache.Setup(t => t.Lookup(path)).Returns(TrueCacheLookup.Valid(CreateEntry(path, _keyframes)));
        var metadata = Extractor(true, _keyframes);
        var probe = Extractor(false, _keyframes);

        Assert.Same(_keyframes, await Create(metadata, probe).GetKeyframesAsync(Guid.NewGuid(), path, MediaProtocol.File, CancellationToken.None));
        VerifyNotRead(metadata, probe);
    }

    [Fact]
    public async Task GetKeyframesAsync_RejectedVideo_IsNotRead()
    {
        var path = CreateFile("Film.mkv");
        _trueCache.Setup(t => t.Lookup(path)).Returns(TrueCacheLookup.Rejected("missing keyframes.json"));
        var metadata = Extractor(true, _keyframes);
        var probe = Extractor(false, _keyframes);

        Assert.Null(await Create(metadata, probe).GetKeyframesAsync(Guid.NewGuid(), path, MediaProtocol.File, CancellationToken.None));
        VerifyNotRead(metadata, probe);
    }

    [Fact]
    public async Task GetKeyframesAsync_Stored_UsesTheDatabase()
    {
        var itemId = Guid.NewGuid();
        _repository.Setup(r => r.GetKeyframeData(itemId)).Returns([_keyframes]);
        var probe = Extractor(false, null);

        Assert.Same(_keyframes, await Create(probe).GetKeyframesAsync(itemId, CreateFile("Film.ts"), MediaProtocol.File, CancellationToken.None));
        VerifyNotRead(probe);
    }

    [Fact]
    public async Task GetKeyframesAsync_RemoteSource_IsNotRead()
    {
        var metadata = Extractor(true, _keyframes);
        var probe = Extractor(false, _keyframes);

        Assert.Null(await Create(metadata, probe).GetKeyframesAsync(Guid.NewGuid(), "https://bucket.example.com/Film.mkv?X-Amz-Signature=1", MediaProtocol.Http, CancellationToken.None));
        VerifyNotRead(metadata, probe);
    }

    [Fact]
    public async Task GetKeyframesAsync_AllowedExtension_ReadsTheContainerIndex()
    {
        var metadata = Extractor(true, _keyframes);
        var probe = Extractor(false, _keyframes);

        Assert.Same(_keyframes, await Create(metadata, probe).GetKeyframesAsync(Guid.NewGuid(), CreateFile("Film.mkv"), MediaProtocol.File, CancellationToken.None));
        VerifyNotRead(probe);
    }

    [Fact]
    public async Task GetKeyframesAsync_OtherExtension_ProbesTheFile()
    {
        var metadata = Extractor(true, _keyframes);
        var probe = Extractor(false, _keyframes);

        Assert.Same(_keyframes, await Create(metadata, probe).GetKeyframesAsync(Guid.NewGuid(), CreateFile("Film.ts"), MediaProtocol.File, CancellationToken.None));
        VerifyNotRead(metadata);
    }

    [Fact]
    public async Task GetKeyframesAsync_SlowProbe_GivesUpAndKeepsReading()
    {
        using var gate = new ManualResetEventSlim();
        var probe = Extractor(false, _keyframes, gate);
        var source = Create(probe);
        var itemId = Guid.NewGuid();
        var path = CreateFile("Film.ts");

        // The second request joins the read the first one started.
        Assert.Null(await source.GetKeyframesAsync(itemId, path, MediaProtocol.File, CancellationToken.None));
        Assert.Null(await source.GetKeyframesAsync(itemId, path, MediaProtocol.File, CancellationToken.None));
        probe.Verify(e => e.TryExtractKeyframes(itemId, path, out It.Ref<KeyframeData?>.IsAny), Times.Once());

        gate.Set();
        Assert.Same(_keyframes, await source.GetKeyframesAsync(itemId, path, MediaProtocol.File, CancellationToken.None));
    }

    [Fact]
    public async Task GetKeyframesAsync_ProbeFindsNothing_IsNotRetried()
    {
        var probe = Extractor(false, null);
        var source = Create(probe);
        var itemId = Guid.NewGuid();
        var path = CreateFile("Film.ts");

        Assert.Null(await source.GetKeyframesAsync(itemId, path, MediaProtocol.File, CancellationToken.None));
        Assert.Null(await source.GetKeyframesAsync(itemId, path, MediaProtocol.File, CancellationToken.None));
        probe.Verify(e => e.TryExtractKeyframes(itemId, path, out It.Ref<KeyframeData?>.IsAny), Times.Once());
    }

    [Fact]
    public async Task GetKeyframesAsync_NoTimeout_DoesNotProbe()
    {
        _options.KeyframeExtractionTimeoutSeconds = 0;
        var probe = Extractor(false, _keyframes);

        Assert.Null(await Create(probe).GetKeyframesAsync(Guid.NewGuid(), CreateFile("Film.ts"), MediaProtocol.File, CancellationToken.None));
        VerifyNotRead(probe);
    }

    private static TrueCacheEntry CreateEntry(string path, KeyframeData keyframes)
        => new(path, "/media/__true_cache__/Film.mkv", "hash", new TrueCacheManifest(), null, null, keyframes, new Dictionary<ImageType, string>(), new Dictionary<int, string>());

    private static Mock<IKeyframeExtractor> Extractor(bool isMetadataBased, KeyframeData? result, ManualResetEventSlim? gate = null)
    {
        var extractor = new Mock<IKeyframeExtractor>();
        extractor.SetupGet(e => e.IsMetadataBased).Returns(isMetadataBased);
        extractor.Setup(e => e.TryExtractKeyframes(It.IsAny<Guid>(), It.IsAny<string>(), out It.Ref<KeyframeData?>.IsAny))
            .Returns(new TryExtract((Guid _, string _, out KeyframeData? keyframeData) =>
            {
                gate?.Wait(TimeSpan.FromSeconds(30));
                keyframeData = result;
                return result is not null;
            }));
        return extractor;
    }

    private static void VerifyNotRead(params Mock<IKeyframeExtractor>[] extractors)
    {
        foreach (var extractor in extractors)
        {
            extractor.Verify(e => e.TryExtractKeyframes(It.IsAny<Guid>(), It.IsAny<string>(), out It.Ref<KeyframeData?>.IsAny), Times.Never());
        }
    }

    private string CreateFile(string name)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, [0]);
        return path;
    }

    private KeyframeSource Create(params Mock<IKeyframeExtractor>[] extractors)
    {
        var configuration = new Mock<IServerConfigurationManager>();
        configuration.Setup(c => c.GetConfiguration("encoding")).Returns(_options);
        return new KeyframeSource(
            _repository.Object,
            _trueCache.Object,
            Mock.Of<ILibraryManager>(),
            configuration.Object,
            Array.ConvertAll(extractors, e => e.Object),
            NullLogger<KeyframeSource>.Instance);
    }
}
