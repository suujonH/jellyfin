using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.MediaEncoding.Hls.Published;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Hls;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using static Jellyfin.Server.Implementations.Tests.Hls.HlsTestEnvironment;

namespace Jellyfin.Server.Implementations.Tests.Hls;

public sealed class HlsPlaybackServiceTests : IDisposable
{
    private const string ArchivedTarget = "s3://archive/Film.mkv";

    private readonly DefaultHttpContext _httpContext = new();
    private readonly Mock<ILibraryManager> _libraryManager = new();
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

    [Fact]
    public async Task ArrangeMediaSources_Published_ListsAutoTheOriginalAndTheQualitiesFromTheHighestDown()
    {
        var (environment, service) = Create();
        var (item, _) = await PublishAsync(environment);
        environment.Save(new HlsOptions { Enabled = true, Qualities = Qualities, AllowOriginal = true });

        var sources = service.ArrangeMediaSources(new Movie { Id = item }, [Original(item)], CreateUser(originals: true));

        Assert.Equal(["HlsSourceAuto", "HlsSourceOriginal (1080p 24fps)", "1080p", "720p"], sources.Select(s => s.Name));
        Assert.Equal([0, null, 1080, 720], sources.Select(s => service.GetQuality(item, s.Id)));
        var auto = sources[0];
        Assert.Equal(MediaProtocol.Http, auto.Protocol);
        Assert.False(auto.SupportsDirectPlay);
        Assert.True(auto.SupportsTranscoding);
        Assert.Equal(MediaStreamProtocol.hls, auto.TranscodingSubProtocol);
        Assert.Equal("/HlsPlayback/Versions/" + auto.Id + "/master.m3u8", auto.Path);
        Assert.Equal(1, auto.DefaultAudioStreamIndex);
        Assert.Equal(1080, auto.VideoStream.Height);
        var subtitle = Assert.Single(auto.MediaStreams, s => s.Type == MediaStreamType.Subtitle);
        Assert.Equal(2, subtitle.Index);
        Assert.False(subtitle.IsExternal);
        Assert.Equal(SubtitleDeliveryMethod.External, subtitle.DeliveryMethod);
    }

    [Fact]
    public async Task ArrangeMediaSources_OriginalNotAllowed_ListsOnlyTheHls()
    {
        var (environment, service) = Create();
        var (item, _) = await PublishAsync(environment);

        // The original is not allowed at all, whatever the user may do.
        Assert.Equal(
            ["HlsSourceAuto", "1080p", "720p"],
            service.ArrangeMediaSources(new Movie { Id = item }, [Original(item)], CreateUser(originals: true)).Select(s => s.Name));

        // Allowed, but the user may not and the request is not from the local network.
        environment.Save(new HlsOptions { Enabled = true, Qualities = Qualities, AllowOriginal = true, AllowLocalOriginal = true });
        Assert.Equal(3, service.ArrangeMediaSources(new Movie { Id = item }, [Original(item)], CreateUser()).Count);
        Assert.False(service.IsOriginalAllowed(item, CreateUser()));
    }

    [Fact]
    public async Task ArrangeMediaSources_LocalRequest_HasTheOriginalWhenLocalOriginalsAreAllowed()
    {
        var (environment, service) = Create(isLocal: true);
        var (item, _) = await PublishAsync(environment);
        environment.Save(new HlsOptions { Enabled = true, Qualities = Qualities, AllowOriginal = true });
        Assert.Equal(3, service.ArrangeMediaSources(new Movie { Id = item }, [Original(item)], CreateUser()).Count);

        environment.Save(new HlsOptions { Enabled = true, Qualities = Qualities, AllowOriginal = true, AllowLocalOriginal = true });
        Assert.Equal(4, service.ArrangeMediaSources(new Movie { Id = item }, [Original(item)], CreateUser()).Count);
        Assert.True(service.IsOriginalAllowed(item, null));
    }

    [Fact]
    public async Task ArrangeMediaSources_ApiKey_AlwaysHasTheOriginal()
    {
        var (environment, service) = Create();
        var (item, _) = await PublishAsync(environment);
        _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("Jellyfin-IsApiKey", "True")], "Custom"));

        Assert.Equal(4, service.ArrangeMediaSources(new Movie { Id = item }, [Original(item)], CreateUser()).Count);
        Assert.True(service.IsOriginalAllowed(item, null));
    }

    [Fact]
    public async Task ArrangeMediaSources_SeveralOriginals_AreNamedApartAndAnArchivedOneIsLeftOut()
    {
        var (environment, service) = Create();
        var (item, _) = await PublishAsync(environment);
        environment.Save(new HlsOptions { Enabled = true, Qualities = Qualities, AllowOriginal = true });
        var archived = Original(Guid.NewGuid(), "Archived");
        archived.ShortcutTarget = ArchivedTarget;

        var sources = service.ArrangeMediaSources(
            new Movie { Id = item },
            [Original(item, "Director's Cut"), Original(Guid.NewGuid(), "Theatrical", 1280, 720), archived],
            CreateUser(originals: true));

        Assert.Equal(
            ["HlsSourceAuto", "HlsSourceOriginal (1080p 24fps) · Director's Cut", "HlsSourceOriginal (720p 24fps) · Theatrical", "1080p", "720p"],
            sources.Select(s => s.Name));
    }

    [Fact]
    public async Task ArrangeMediaSources_OriginalNotAllowed_KeepsAnUnpublishedAlternateVersion()
    {
        var (environment, service) = Create();
        var (item, _) = await PublishAsync(environment);
        var alternate = Guid.NewGuid();

        var sources = service.ArrangeMediaSources(new Movie { Id = item }, [Original(item), Original(alternate, "Theatrical")], CreateUser());

        Assert.Equal([null, alternate.ToString("N"), null, null], sources.Select(s => service.GetQuality(item, s.Id) is null ? s.Id : null));
        Assert.Equal("HlsSourceOriginal (1080p 24fps)", sources[1].Name);
        Assert.True(service.IsOriginalAllowed(alternate, CreateUser()));
    }

    [Fact]
    public async Task ArrangeMediaSources_Tracks_KeepTheDispositionsOfTheOriginal()
    {
        var (environment, service) = Create();
        var (item, _) = await PublishAsync(environment);
        var original = Original(item);
        original.MediaStreams =
        [
            .. original.MediaStreams,
            new MediaStream { Index = 1, Type = MediaStreamType.Audio, IsOriginal = true },
            new MediaStream { Index = 2, Type = MediaStreamType.Subtitle, IsForced = true }
        ];

        var sources = service.ArrangeMediaSources(new Movie { Id = item }, [original], CreateUser());

        Assert.All(sources, s => Assert.True(Assert.Single(s.MediaStreams, m => m.Type == MediaStreamType.Subtitle).IsForced));
        Assert.All(sources, s => Assert.True(Assert.Single(s.MediaStreams, m => m.Type == MediaStreamType.Audio).IsOriginal));
        Assert.False(Assert.Single(service.ArrangeMediaSources(new Movie { Id = item }, [], CreateUser())[0].MediaStreams, m => m.Type == MediaStreamType.Subtitle).IsForced);
    }

    [Fact]
    public async Task ArrangeMediaSources_ResumedAlternateVersion_StaysFirst()
    {
        var (environment, service) = Create();
        var (item, _) = await PublishAsync(environment);
        var alternate = Guid.NewGuid();

        var sources = service.ArrangeMediaSources(new Movie { Id = item }, [Original(alternate), Original(item)], CreateUser());

        Assert.Equal(alternate.ToString("N"), sources[0].Id);
        Assert.Equal(0, service.GetQuality(item, sources[1].Id));
        Assert.Equal(4, sources.Count);
    }

    [Fact]
    public async Task ArrangeMediaSources_UserMayNotPlayMedia_IsEmpty()
    {
        var (environment, service) = Create();
        var (item, _) = await PublishAsync(environment);
        var user = CreateUser(originals: true);
        user.SetPermission(PermissionKind.EnableMediaPlayback, false);

        Assert.Empty(service.ArrangeMediaSources(new Movie { Id = item }, [Original(item)], user));
    }

    [Fact]
    public async Task ArrangeMediaSources_NotPublished_KeepsTheOriginalsButPublishedAlternateVersions()
    {
        var (environment, service) = Create();
        var (published, _) = await PublishAsync(environment);
        var item = Guid.NewGuid();
        var other = Guid.NewGuid();
        var archived = Original(other);
        archived.ShortcutTarget = ArchivedTarget;
        var sources = new[] { Original(item), Original(published), archived };

        // An archive is left to the existing checks; only the published alternate version follows the rules of pre-encoded HLS.
        Assert.Equal(
            [item.ToString("N"), other.ToString("N")],
            service.ArrangeMediaSources(new Movie { Id = item }, sources, CreateUser()).Select(s => s.Id));
        Assert.True(service.IsOriginalAllowed(item, null));
        Assert.False(service.IsPublished(item));
    }

    [Fact]
    public async Task GetQuality_KnowsOnlyThePublishedSourcesOfTheItem()
    {
        var (environment, service) = Create();
        var (item, _) = await PublishAsync(environment);
        var sources = service.ArrangeMediaSources(new Movie { Id = item }, [], CreateUser());

        Assert.Equal(0, service.GetQuality(item, sources[0].Id.ToUpperInvariant()));
        Assert.Null(service.GetQuality(item, item.ToString("N")));
        Assert.Null(service.GetQuality(item, null));
        Assert.Null(service.GetQuality(Guid.NewGuid(), sources[0].Id));

        environment.Save(new HlsOptions { Qualities = Qualities });
        Assert.Null(service.GetQuality(item, sources[0].Id));
    }

    [Fact]
    public async Task PrepareForPlayback_PlaysThePlaylistOfANewGrant()
    {
        var (environment, service) = Create();
        var (item, plan) = await PublishAsync(environment);
        var source = service.ArrangeMediaSources(new Movie { Id = item }, [], CreateUser())[2];

        Assert.True(service.PrepareForPlayback(item, source, "session", null, null, "http://server/jellyfin/"));

        Assert.False(source.SupportsDirectPlay);
        Assert.False(source.SupportsDirectStream);
        Assert.True(source.SupportsTranscoding);
        var url = source.TranscodingUrl.Split('?');
        Assert.Equal("PlaySessionId=session&MediaSourceId=" + source.Id, url[1]);
        var path = url[0].Split('/');
        Assert.Equal([string.Empty, "HlsPlayback"], path[..2]);
        Assert.Equal(item, service.VerifyGrant(path[2]));
        Assert.Equal("720p-a1.m3u8", path[3]);
        Assert.Equal("http://server/jellyfin" + source.TranscodingUrl, source.Path);
        var subtitle = Assert.Single(source.MediaStreams, s => s.Type == MediaStreamType.Subtitle);
        Assert.Equal("/HlsPlayback/" + path[2] + "/r/subtitles/2/" + Attempt(plan, "s2") + "/subtitle.vtt", subtitle.DeliveryUrl);
        Assert.Equal(SubtitleDeliveryMethod.External, subtitle.DeliveryMethod);
        Assert.False(subtitle.IsExternalUrl);

        // A track that is not published plays every track and selects nothing.
        Assert.True(service.PrepareForPlayback(item, source, "session", 5, 3, "http://server"));
        Assert.Contains("/720p.m3u8?", source.TranscodingUrl, StringComparison.Ordinal);
        Assert.Equal(1, source.DefaultAudioStreamIndex);
        Assert.Equal(-1, source.DefaultSubtitleStreamIndex);

        // The requested tracks are selected.
        Assert.True(service.PrepareForPlayback(item, source, "session", 1, 2, "http://server"));
        Assert.Equal(2, source.DefaultSubtitleStreamIndex);
        Assert.Contains("/720p-a1.m3u8?", source.TranscodingUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareForPlayback_SourceThatCannotBePlayed_IsRefused()
    {
        var (environment, service) = Create();
        var (item, _) = await PublishAsync(environment);
        var source = service.ArrangeMediaSources(new Movie { Id = item }, [], CreateUser())[0];

        Assert.False(service.PrepareForPlayback(item, Original(item), "session", null, null, "http://server"));
        environment.Save(new HlsOptions { Qualities = Qualities });
        Assert.False(service.PrepareForPlayback(item, source, "session", null, null, "http://server"));
        Assert.Null(source.TranscodingUrl);
    }

    [Fact]
    public async Task PrefersOriginal_OnlyForALocalRequestThatMayPlayIt()
    {
        var (environment, service) = Create();
        var (item, _) = await PublishAsync(environment);
        environment.Save(new HlsOptions { Enabled = true, Qualities = Qualities, AllowOriginal = true, AllowLocalOriginal = true });

        Assert.False(service.PrefersOriginal(new Movie { Id = item, Path = "/media/Film.mkv" }, CreateUser(originals: true)));
        Assert.False(service.PrefersOriginal(new Movie { Id = Guid.NewGuid(), Path = "/media/Film.mkv" }, CreateUser(originals: true)));
    }

    [Fact]
    public async Task GetPlaylistUrl_SourceOrVersionItem_LeadsToItsPlaylistUnderANewGrant()
    {
        var (_, service) = Create();
        var (item, _) = await PublishAsync(_environment!);
        var user = CreateUser();
        var original = new Movie { Id = item };
        _libraryManager.Setup(l => l.GetItemById<BaseItem>(item, user)).Returns(original);
        var sourceId = HlsVersionCatalog.GetVersionId(_libraryManager.Object, item, 720);
        var version = new Movie { Id = sourceId, ProviderIds = new() { [HlsVersionCatalog.Marker] = item.ToString("N") + ":720" } };

        Assert.Equal(item, service.GetOriginalId(version));
        Assert.Null(service.GetOriginalId(original));
        foreach (var url in new[] { service.GetPlaylistUrl(original, sourceId.ToString("N"), 1, user), service.GetPlaylistUrl(version, null, 1, user) })
        {
            Assert.NotNull(url);
            var parts = url.Split('/');
            Assert.Equal([string.Empty, "HlsPlayback"], parts[..2]);
            Assert.Equal(item, service.VerifyGrant(parts[2]));
            Assert.Equal("720p-a1.m3u8", parts[3]);
        }

        // An original source, or none, is no source of pre-encoded HLS, also when a version item names it; an API key sees every item.
        Assert.Null(service.GetPlaylistUrl(original, item.ToString("N"), null, user));
        Assert.Null(service.GetPlaylistUrl(version, item.ToString("N"), null, user));
        Assert.Null(service.GetPlaylistUrl(original, null, null, user));
        Assert.EndsWith("/master.m3u8", service.GetPlaylistUrl(version, HlsVersionCatalog.GetVersionId(_libraryManager.Object, item, 0).ToString("N"), null, null), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetPlaylistUrl_UserWhoCannotPlayTheOriginal_IsRefused()
    {
        var (_, service) = Create();
        var (item, _) = await PublishAsync(_environment!);
        var user = CreateUser();
        var version = new Movie { Id = HlsVersionCatalog.GetVersionId(_libraryManager.Object, item, 0), ProviderIds = new() { [HlsVersionCatalog.Marker] = item.ToString("N") + ":0" } };

        // The original is not visible to the user.
        Assert.Null(service.GetPlaylistUrl(version, null, null, user));

        _libraryManager.Setup(l => l.GetItemById<BaseItem>(item, user)).Returns(new Movie { Id = item });
        Assert.NotNull(service.GetPlaylistUrl(version, null, null, user));

        // The marker alone names no source.
        var forged = new Movie { Id = Guid.NewGuid(), ProviderIds = version.ProviderIds };
        Assert.Null(service.GetPlaylistUrl(forged, null, null, user));

        user.SetPermission(PermissionKind.EnableMediaPlayback, false);
        Assert.Null(service.GetPlaylistUrl(version, null, null, user));
    }

    private static User CreateUser(bool originals = false)
    {
        var user = new User("user", "default", "default");
        user.AddDefaultPermissions();
        user.SetPermission(PermissionKind.EnableOriginalPlayback, originals);
        return user;
    }

    // An original source of an item, with a 24 fps video stream.
    private static MediaSourceInfo Original(Guid item, string name = "", int width = 1920, int height = 1080)
        => new()
        {
            Id = item.ToString("N"),
            Name = name,
            Protocol = MediaProtocol.File,
            MediaStreams = [new MediaStream { Index = 0, Type = MediaStreamType.Video, Width = width, Height = height, RealFrameRate = 23.976f }]
        };

    private (HlsTestEnvironment Environment, HlsPlaybackService Service) Create(bool isPublic = false, bool isLocal = false)
    {
        IHlsStoreFactory? factory = null;
        if (isPublic)
        {
            var local = new HlsStoreFactory(Mock.Of<INetworkStorageManager>());
            var mock = new Mock<IHlsStoreFactory>();
            mock.Setup(f => f.Create(It.IsAny<string>())).Returns<string>(root => new PublicHlsStore(local.Create(root), "https://cdn.example/", TimeSpan.Zero));
            factory = mock.Object;
        }

        // The ids of the sources stand for those of the library, which hashes the path the same way every time.
        var libraryManager = _libraryManager;
        libraryManager.Setup(l => l.GetNewItemId(It.IsAny<string>(), It.IsAny<Type>()))
            .Returns<string, Type>((key, _) => new Guid(MD5.HashData(Encoding.UTF8.GetBytes(key))));
        var localization = new Mock<ILocalizationManager>();
        localization.Setup(l => l.GetLocalizedString(It.IsAny<string>())).Returns<string>(phrase => phrase);
        var networkManager = new Mock<INetworkManager>();
        networkManager.Setup(n => n.IsInLocalNetwork(It.IsAny<IPAddress>())).Returns(isLocal);
        var networkStorage = new Mock<INetworkStorageManager>();
        networkStorage.Setup(n => n.IsArchived(ArchivedTarget)).Returns(true);
        _httpContext.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.1");

        _environment = new HlsTestEnvironment(factory);
        var service = new HlsPlaybackService(
            _environment.Settings,
            _environment.Index,
            _environment.Store,
            _environment.StoreFactory,
            _environment.ConfigurationManager,
            libraryManager.Object,
            localization.Object,
            networkManager.Object,
            networkStorage.Object,
            new HttpContextAccessor { HttpContext = _httpContext },
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
