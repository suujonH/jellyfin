using System;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Model.Configuration;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Hls;

public sealed class HlsSettingsTests : IDisposable
{
    private readonly HlsTestEnvironment _environment = new();

    public void Dispose() => _environment.Dispose();

    [Fact]
    public void Current_IsWhatWasSaved()
    {
        var current = _environment.Settings.Current;

        Assert.True(current.Options.Enabled);
        Assert.Null(current.Error);
        Assert.Equal(0, current.Version);
        Assert.Equal(TimeSpan.FromMinutes(5), current.Lease);
    }

    [Fact]
    public void Save_IsAppliedAsTheNextVersion_AndOtherConfigurationsAreIgnored()
    {
        _environment.Save(new HlsOptions());

        var current = _environment.Settings.Current;
        Assert.False(current.Options.Enabled);
        Assert.Equal(1, current.Version);

        _environment.RaiseUpdated("encoding", new EncodingOptions());
        Assert.Same(current, _environment.Settings.Current);
    }

    [Fact]
    public void Save_Invalid_TurnsItOffAndSaysWhy()
    {
        _environment.Save(new HlsOptions { Enabled = true, Qualities = [] });

        var current = _environment.Settings.Current;
        Assert.False(current.Options.Enabled);
        Assert.NotNull(current.Error);
        Assert.False(current.IsLibraryEligible(HlsTestEnvironment.LibraryId));
    }

    [Fact]
    public void IsLibraryEligible_NoLibrariesMeansEvery_WhileOn()
    {
        Assert.True(_environment.Settings.Current.IsLibraryEligible(HlsTestEnvironment.LibraryId));

        _environment.Save(new HlsOptions { Enabled = true, Libraries = [HlsTestEnvironment.OtherLibraryId] });
        Assert.False(_environment.Settings.Current.IsLibraryEligible(HlsTestEnvironment.LibraryId));
        Assert.True(_environment.Settings.Current.IsLibraryEligible(HlsTestEnvironment.OtherLibraryId));

        _environment.Save(new HlsOptions { Libraries = [HlsTestEnvironment.OtherLibraryId] });
        Assert.False(_environment.Settings.Current.IsLibraryEligible(HlsTestEnvironment.OtherLibraryId));
    }
}
