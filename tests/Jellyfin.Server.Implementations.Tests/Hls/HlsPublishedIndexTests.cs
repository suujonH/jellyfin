using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Published;
using Jellyfin.Server.Implementations.Hls;
using Xunit;
using static Jellyfin.Server.Implementations.Tests.Hls.HlsTestEnvironment;

namespace Jellyfin.Server.Implementations.Tests.Hls;

public sealed class HlsPublishedIndexTests : IDisposable
{
    private readonly HlsTestEnvironment _environment = new();

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public void Dispose() => _environment.Dispose();

    [Fact]
    public async Task Load_HoldsTheFiles_AndWhatIsPublishedOfTheirContents()
    {
        var (item, plan) = await _environment.PublishAsync(Sha('a'));
        var copy = await AddCopyAsync(Sha('a'));
        var unidentified = await _environment.AddSourceAsync();

        await _environment.Index.LoadAsync(CancellationToken);

        Assert.Equal(new HlsIndexedSource(item, LibraryId, LibraryFile(item).Path, Sha('a')), _environment.Index.GetSource(item));
        Assert.Equal(new HlsIndexedSource(unidentified, LibraryId, LibraryFile(unidentified).Path, null), _environment.Index.GetSource(unidentified));
        Assert.Null(_environment.Index.GetSource(Guid.NewGuid()));
        Assert.Equal(new[] { item, copy }.Order(), _environment.Index.GetItemsForSha(Sha('a')).Order());
        Assert.Empty(_environment.Index.GetItemsForSha(Sha('b')));

        var view = _environment.Index.GetMediaForItem(item);
        Assert.NotNull(view);
        Assert.Same(view, _environment.Index.GetMediaForItem(copy));
        Assert.Null(_environment.Index.GetMediaForItem(unidentified));
        Assert.Equal(Sha('a'), view.Sha256);
        Assert.Equal(plan.MediaKey, view.MediaKey);
        Assert.Equal(Probe.DurationSeconds, view.DurationSeconds);
        Assert.Equal(1, view.ExpectedAudio);
        Assert.True(view.Playable);

        // Video from the highest quality down, then audio and subtitles by stream index; the preview is not part of it.
        Assert.Equal(["v1080", "v720"], view.Video.Select(r => r.Key));
        Assert.Equal(["a1"], view.Audio.Select(r => r.Key));
        Assert.Equal(["s2"], view.Subtitles.Select(r => r.Key));
        Assert.Equal(plan.Renditions.Single(r => r.Key == "v720").RelativeDirectory, view.Video[1].RelativeDirectory);
        Assert.Equal(720, view.Video[1].Summary.Quality);
    }

    [Fact]
    public async Task RefreshItems_FollowsAChangedOrRemovedFile()
    {
        var (item, _) = await _environment.PublishAsync(Sha('a'));
        var copy = await AddCopyAsync(Sha('a'));
        await _environment.Index.LoadAsync(CancellationToken);

        // A new length is new content, until a worker identifies it again.
        await _environment.Store.UpsertSourcesAsync([LibraryFile(copy) with { Length = 2000 }], _environment.Now, CancellationToken);
        await _environment.Store.RemoveSourceAsync(item, _environment.Now, CancellationToken);
        await _environment.Index.RefreshItemsAsync([item, copy, item], CancellationToken);

        Assert.Null(_environment.Index.GetSource(item));
        Assert.Null(_environment.Index.GetSource(copy)!.Sha256);
        Assert.Null(_environment.Index.GetMediaForItem(copy));
        Assert.Empty(_environment.Index.GetItemsForSha(Sha('a')));
    }

    [Fact]
    public async Task RefreshMedia_DropsAContentWithNothingPublished()
    {
        var (item, plan) = await _environment.PublishAsync(Sha('a'));
        await _environment.Index.LoadAsync(CancellationToken);
        var renditions = plan.Renditions.ToDictionary(r => r.Key, r => r.RelativeDirectory);

        // Without its audio the content cannot be played, though its video is published.
        await RetireAsync(renditions, "a1");
        await _environment.Index.RefreshMediaAsync([Sha('a')], CancellationToken);
        Assert.False(_environment.Index.GetMediaForItem(item)!.Playable);

        foreach (var key in new[] { "v1080", "v720", "s2", "preview" })
        {
            await RetireAsync(renditions, key);
        }

        await _environment.Index.RefreshMediaAsync([Sha('a')], CancellationToken);
        Assert.Null(_environment.Index.GetMediaForItem(item));
        Assert.Equal(Sha('a'), _environment.Index.GetSource(item)!.Sha256);
    }

    [Fact]
    public async Task AFailedRefresh_IsLeftToTheNextOne()
    {
        await _environment.Index.LoadAsync(CancellationToken);
        var (item, _) = await _environment.PublishAsync(Sha('a'));

        await _environment.RenameTableAsync("HlsSources", "HlsSourcesGone");
        await Assert.ThrowsAnyAsync<Exception>(() => _environment.Index.RefreshItemsAsync([item], CancellationToken));
        await _environment.RenameTableAsync("HlsSourcesGone", "HlsSources");
        await _environment.RenameTableAsync("HlsMedia", "HlsMediaGone");
        await Assert.ThrowsAnyAsync<Exception>(() => _environment.Index.RefreshMediaAsync([Sha('a')], CancellationToken));
        await _environment.RenameTableAsync("HlsMediaGone", "HlsMedia");
        Assert.Null(_environment.Index.GetSource(item));

        await _environment.Index.RefreshStaleAsync(CancellationToken);

        Assert.True(_environment.Index.GetMediaForItem(item)!.Playable);
    }

    [Fact]
    public async Task EveryRefresh_RaisesChangedWithTheItemsItMayHaveChanged()
    {
        var (item, plan) = await _environment.PublishAsync(Sha('a'));
        var copy = await AddCopyAsync(Sha('a'));
        var changes = new List<Guid[]>();
        _environment.Index.Changed += (_, items) => changes.Add([.. items.Order()]);
        Assert.False(_environment.Index.IsLoaded);

        await _environment.Index.LoadAsync(CancellationToken);
        Assert.True(_environment.Index.IsLoaded);
        Assert.Equal(new[] { item, copy }.Order(), Assert.Single(changes));
        Assert.Equal(new[] { item, copy }.Order(), _environment.Index.GetPublishedItems().Order());

        // A removed file is part of the next load too.
        await _environment.Store.RemoveSourceAsync(copy, _environment.Now, CancellationToken);
        await _environment.Index.RefreshItemsAsync([copy], CancellationToken);
        Assert.Equal([copy], changes[^1]);

        await RetireAsync(plan.Renditions.ToDictionary(r => r.Key, r => r.RelativeDirectory), "v720");
        await _environment.Index.RefreshMediaAsync([Sha('a')], CancellationToken);
        Assert.Equal([item], changes[^1]);

        // Nothing to report raises nothing.
        await _environment.Index.RefreshMediaAsync([Sha('b')], CancellationToken);
        Assert.Equal(3, changes.Count);
    }

    // Another library file of the content, recorded with its hash as a worker would have identified it.
    private async Task<Guid> AddCopyAsync(string sha)
    {
        var copy = await _environment.AddSourceAsync();
        var lease = await _environment.Store.ClaimAsync(new HlsClaimRequest("worker", Lease, _ => true, _environment.Now), CancellationToken);
        Assert.Equal(copy, lease!.Input!.ItemId);
        var result = await _environment.Store.IdentifyAsync(
            new HlsIdentifyRequest(lease.Id, lease.LeaseToken, sha, Probe, _environment.Root, _ => Qualities, Profile, _ => true, _environment.Now),
            CancellationToken);
        Assert.Equal(HlsIdentifyOutcome.Done, result.Outcome);
        return copy;
    }

    private async Task RetireAsync(Dictionary<string, string> renditions, string key)
        => Assert.True(await _environment.Store.RetireRenditionAsync(Sha('a'), key, renditions[key], "Gone.", _environment.Now, CancellationToken));
}
