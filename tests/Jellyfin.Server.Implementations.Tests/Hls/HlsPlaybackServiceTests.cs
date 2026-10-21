using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Published;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Hls;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Server.Implementations.Tests.Hls.HlsTestEnvironment;

namespace Jellyfin.Server.Implementations.Tests.Hls;

public sealed class HlsPlaybackServiceTests : IDisposable
{
    private HlsTestEnvironment? _environment;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => _environment?.Dispose();

    [Fact]
    public async Task IssueGrant_FirstGrant_CreatesAndSavesTheKey_WhichLaterGrantsUse()
    {
        var (environment, service) = Create();
        var (item, _) = await PublishAsync(environment);
        Assert.Empty(environment.Options.PlaybackSigningKey);

        var first = service.IssueGrant(item);
        var key = environment.Options.PlaybackSigningKey;
        var second = service.IssueGrant(item);

        Assert.Equal(32, Convert.FromBase64String(key).Length);
        Assert.Equal(key, environment.Settings.Current.Options.PlaybackSigningKey);
        Assert.Equal(key, environment.Options.PlaybackSigningKey);
        Assert.Equal(item, service.VerifyGrant(first!));
        Assert.Equal(item, service.VerifyGrant(second!));
    }

    [Fact]
    public async Task IssueGrant_ItemThatCannotBePlayed_IsRefused()
    {
        var (environment, service) = Create();
        var (item, _) = await PublishAsync(environment);
        var unpublished = await environment.AddSourceAsync();
        await environment.Index.LoadAsync(CancellationToken);

        Assert.Null(service.IssueGrant(Guid.NewGuid()));
        Assert.Null(service.IssueGrant(unpublished));

        environment.Save(new HlsOptions { Enabled = true, Qualities = Qualities, Libraries = [OtherLibraryId] });
        Assert.Null(service.IssueGrant(item));

        environment.Save(new HlsOptions { Qualities = Qualities });
        Assert.Null(service.IssueGrant(item));
        Assert.Empty(environment.Options.PlaybackSigningKey);
    }

    [Fact]
    public async Task VerifyGrant_InvalidGrant_IsRefused()
    {
        var (environment, service) = Create();
        var (item, _) = await PublishAsync(environment);

        // No key yet: no grant can be valid.
        Assert.Null(service.VerifyGrant(HlsPlaybackGrant.Create(item, DateTimeOffset.UtcNow.AddHours(1), new byte[32])));

        var grant = service.IssueGrant(item)!;
        Assert.Null(service.VerifyGrant("not a grant"));
        Assert.Null(service.VerifyGrant(grant[..^1]));
        Assert.Null(service.VerifyGrant(HlsPlaybackGrant.Create(item, DateTimeOffset.UtcNow.AddHours(1), new byte[32])));
        var key = Convert.FromBase64String(environment.Options.PlaybackSigningKey);
        Assert.Null(service.VerifyGrant(HlsPlaybackGrant.Create(item, DateTimeOffset.UtcNow.AddSeconds(-1), key)));
    }

    [Fact]
    public async Task GetMasterPlaylist_ListsOnlyWhatIsPublished()
    {
        var (environment, service) = Create();
        var (item, _) = await PublishAsync(environment);

        var master = service.GetMasterPlaylist(item, "master")!;
        Assert.Contains("r/video/1080/index.m3u8", master, StringComparison.Ordinal);
        Assert.Contains("r/video/720/index.m3u8", master, StringComparison.Ordinal);
        Assert.Contains("URI=\"r/audio/1/index.m3u8\"", master, StringComparison.Ordinal);
        Assert.Contains("URI=\"r/subtitles/2/index.m3u8\"", master, StringComparison.Ordinal);
        Assert.DoesNotContain("r/video/1080/", service.GetMasterPlaylist(item, "720p-a1")!, StringComparison.Ordinal);

        Assert.Null(service.GetMasterPlaylist(item, "480p"));
        Assert.Null(service.GetMasterPlaylist(item, "master-a2"));
        Assert.Null(service.GetMasterPlaylist(item, "index"));
        Assert.Null(service.GetMasterPlaylist(Guid.NewGuid(), "master"));
    }

    [Fact]
    public async Task GetMediaPlaylist_VideoAndAudio_ListTheReportedSegmentsInTheirAttempt()
    {
        var (environment, service) = Create();
        var (item, plan) = await PublishAsync(environment);

        foreach (var (kind, index, key) in new[] { (HlsRenditionKind.Video, 1080, "v1080"), (HlsRenditionKind.Audio, 1, "a1") })
        {
            var attempt = Attempt(plan, key);
            var lines = (await service.GetMediaPlaylistAsync(item, kind, index, CancellationToken))!.ReplaceLineEndings("\n").Split('\n');
            Assert.Contains("#EXT-X-VERSION:7", lines);
            Assert.Contains("#EXT-X-TARGETDURATION:6", lines);
            Assert.Contains("#EXT-X-MAP:URI=\"" + attempt + "/init.mp4\"", lines);
            Assert.Equal(10, lines.Count(l => l == "#EXTINF:6.000000, nodesc"));
            Assert.Equal(
                Enumerable.Range(0, 10).Select(i => attempt + "/" + HlsRenditionPaths.GetSegmentFile(i)),
                lines.Where(l => l.EndsWith(".m4s", StringComparison.Ordinal)));
            Assert.Contains("#EXT-X-ENDLIST", lines);
        }
    }

    [Fact]
    public async Task GetMediaPlaylist_Subtitle_IsOneFileForTheWholeContent()
    {
        var (environment, service) = Create();
        var (item, plan) = await PublishAsync(environment);

        var lines = (await service.GetMediaPlaylistAsync(item, HlsRenditionKind.Subtitle, 2, CancellationToken))!.ReplaceLineEndings("\n").Split('\n');

        Assert.Contains("#EXT-X-VERSION:3", lines);
        Assert.Contains("#EXT-X-TARGETDURATION:60", lines);
        Assert.Contains("#EXTINF:60.000000, nodesc", lines);
        Assert.Equal(Attempt(plan, "s2") + "/" + HlsRenditionPaths.SubtitleFile, Assert.Single(lines, l => !l.StartsWith('#') && l.Length > 0));
    }

    [Fact]
    public async Task GetMediaPlaylist_PublicStore_GivesTheDirectAddresses()
    {
        var (environment, service) = Create(isPublic: true);
        var (item, plan) = await PublishAsync(environment);
        var directory = plan.Renditions.Single(r => r.Key == "v720").RelativeDirectory;

        var text = (await service.GetMediaPlaylistAsync(item, HlsRenditionKind.Video, 720, CancellationToken))!;

        Assert.Contains("#EXT-X-MAP:URI=\"https://cdn.example/" + directory + "/init.mp4?sig=1\"", text, StringComparison.Ordinal);
        Assert.Contains("https://cdn.example/" + directory + "/segment-000009.m4s?sig=1", text, StringComparison.Ordinal);
        Assert.Contains(
            "https://cdn.example/" + plan.Renditions.Single(r => r.Key == "s2").RelativeDirectory + "/subtitle.vtt?sig=1",
            (await service.GetMediaPlaylistAsync(item, HlsRenditionKind.Subtitle, 2, CancellationToken))!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetMediaPlaylist_RenditionNotPublished_IsNotFound()
    {
        var (environment, service) = Create();
        var (item, _) = await PublishAsync(environment);

        Assert.Null(await service.GetMediaPlaylistAsync(item, HlsRenditionKind.Video, 480, CancellationToken));
        Assert.Null(await service.GetMediaPlaylistAsync(item, HlsRenditionKind.Audio, 2, CancellationToken));
        Assert.Null(await service.GetMediaPlaylistAsync(item, HlsRenditionKind.Subtitle, 3, CancellationToken));
        Assert.Null(await service.GetMediaPlaylistAsync(item, HlsRenditionKind.Preview, 0, CancellationToken));
        Assert.Null(await service.GetMediaPlaylistAsync(Guid.NewGuid(), HlsRenditionKind.Video, 1080, CancellationToken));
    }

    [Fact]
    public async Task GetFile_FilesOfTheLayout_AreSentFromTheStore()
    {
        var (environment, service) = Create();
        var (item, plan) = await PublishAsync(environment);
        string PathOf(string key, string file) => Path.Combine(environment.GetStorePath(plan.Renditions.Single(r => r.Key == key).RelativeDirectory), file);

        Assert.Equal(new HlsDeliveredFile(PathOf("v1080", "init.mp4"), true, "video/mp4"), service.GetFile(item, HlsRenditionKind.Video, 1080, Attempt(plan, "v1080"), "init.mp4"));
        Assert.Equal(
            new HlsDeliveredFile(PathOf("v720", "segment-000009.m4s"), true, "video/iso.segment"),
            service.GetFile(item, HlsRenditionKind.Video, 720, Attempt(plan, "v720"), "segment-000009.m4s"));
        Assert.Equal(new HlsDeliveredFile(PathOf("a1", "init.mp4"), true, "audio/mp4"), service.GetFile(item, HlsRenditionKind.Audio, 1, Attempt(plan, "a1"), "init.mp4"));
        Assert.Equal(new HlsDeliveredFile(PathOf("s2", "subtitle.vtt"), true, "text/vtt"), service.GetFile(item, HlsRenditionKind.Subtitle, 2, Attempt(plan, "s2"), "subtitle.vtt"));
    }

    [Fact]
    public async Task GetFile_PublishedAgain_StillSendsTheFilesOfTheReplacedAttempt()
    {
        var (environment, service) = Create();
        var (item, plan) = await PublishAsync(environment);
        await environment.Store.EnsureMediaJobAsync(Sha('a'), null, "next-profile", _ => true, environment.Now, CancellationToken);
        var lease = await environment.Store.ClaimAsync(new HlsClaimRequest("worker", Lease, _ => true, environment.Now), CancellationToken);
        var next = (await environment.Store.IdentifyAsync(
            new HlsIdentifyRequest(lease!.Id, lease.LeaseToken, Sha('a'), Probe, environment.Root, _ => Qualities, "next-profile", _ => true, environment.Now),
            CancellationToken)).Plan!;
        environment.WriteFiles(next);
        Assert.True(await environment.Store.SubmitOutputsAsync(lease.Id, lease.LeaseToken, Outputs(next), environment.Now, CancellationToken));
        Assert.NotNull(await environment.Store.PublishAsync(lease.Id, _ => ReplacedGrace, environment.Now, CancellationToken));
        await environment.Index.LoadAsync(CancellationToken);

        // A playback that has the playlist of the old attempt keeps reading it; a new playlist names the new one.
        var old = plan.Renditions.Single(r => r.Key == "v1080").RelativeDirectory;
        Assert.Equal(
            Path.Combine(environment.GetStorePath(old), "init.mp4"),
            service.GetFile(item, HlsRenditionKind.Video, 1080, Attempt(plan, "v1080"), "init.mp4")!.Location);
        Assert.Contains(
            Attempt(next, "v1080") + "/init.mp4",
            (await service.GetMediaPlaylistAsync(item, HlsRenditionKind.Video, 1080, CancellationToken))!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetFile_AttemptOutsideTheLayout_IsNotFound()
    {
        var (environment, service) = Create();
        var (item, plan) = await PublishAsync(environment);

        Assert.Null(service.GetFile(item, HlsRenditionKind.Video, 1080, "..", "init.mp4"));
        Assert.Null(service.GetFile(item, HlsRenditionKind.Video, 1080, Attempt(plan, "v1080").ToUpperInvariant(), "init.mp4"));
        Assert.Null(service.GetFile(item, HlsRenditionKind.Video, 1080, Attempt(plan, "v720") + "/x", "init.mp4"));
        Assert.Null(service.GetFile(item, HlsRenditionKind.Video, 1080, string.Empty, "init.mp4"));
    }

    [Theory]
    [InlineData(HlsRenditionKind.Video, 1080, "segment-000011.m4s")]
    [InlineData(HlsRenditionKind.Video, 1080, "index.m3u8")]
    [InlineData(HlsRenditionKind.Video, 1080, "subtitle.vtt")]
    [InlineData(HlsRenditionKind.Video, 1080, "../init.mp4")]
    [InlineData(HlsRenditionKind.Video, 1080, "segment-1.m4s")]
    [InlineData(HlsRenditionKind.Video, 480, "init.mp4")]
    [InlineData(HlsRenditionKind.Subtitle, 2, "init.mp4")]
    [InlineData(HlsRenditionKind.Preview, 0, "preview.jpg")]
    public async Task GetFile_OtherFile_IsNotFound(HlsRenditionKind kind, int index, string name)
    {
        var (environment, service) = Create();
        var (item, plan) = await PublishAsync(environment);

        Assert.Null(service.GetFile(item, kind, index, Attempt(plan, "v1080"), name));
    }

    [Fact]
    public async Task Playback_PreEncodedHlsOff_FindsNothing()
    {
        var (environment, service) = Create();
        var (item, plan) = await PublishAsync(environment);
        environment.Save(new HlsOptions { Qualities = Qualities });

        Assert.Null(service.GetMasterPlaylist(item, "master"));
        Assert.Null(await service.GetMediaPlaylistAsync(item, HlsRenditionKind.Video, 1080, CancellationToken));
        Assert.Null(service.GetFile(item, HlsRenditionKind.Video, 1080, Attempt(plan, "v1080"), "init.mp4"));
    }

    private (HlsTestEnvironment Environment, HlsPlaybackService Service) Create(bool isPublic = false)
    {
        IHlsStoreFactory? factory = null;
        if (isPublic)
        {
            var local = new HlsStoreFactory(Mock.Of<INetworkStorageManager>());
            var mock = new Mock<IHlsStoreFactory>();
            mock.Setup(f => f.Create(It.IsAny<string>())).Returns<string>(root => new PublicHlsStore(local.Create(root), "https://cdn.example/", TimeSpan.Zero));
            factory = mock.Object;
        }

        _environment = new HlsTestEnvironment(factory);
        var service = new HlsPlaybackService(
            _environment.Settings,
            _environment.Index,
            _environment.Store,
            _environment.StoreFactory,
            _environment.ConfigurationManager,
            NullLogger<HlsPlaybackService>.Instance);
        return (_environment, service);
    }

    // The attempt directory of a rendition of a plan.
    private static string Attempt(HlsEncodePlan plan, string key)
    {
        var directory = plan.Renditions.Single(r => r.Key == key).RelativeDirectory;
        return directory[(directory.LastIndexOf('/') + 1)..];
    }

    // A library file whose content is published, in the index.
    private static async Task<(Guid Item, HlsEncodePlan Plan)> PublishAsync(HlsTestEnvironment environment)
    {
        var published = await environment.PublishAsync(Sha('a'));
        await environment.Index.LoadAsync(CancellationToken);
        return published;
    }
}
