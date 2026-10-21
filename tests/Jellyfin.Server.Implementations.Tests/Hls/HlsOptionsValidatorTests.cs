using System;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Model.Configuration;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Hls;

public class HlsOptionsValidatorTests
{
    [Fact]
    public void Validate_Defaults_AreValid()
        => Assert.Null(HlsOptionsValidator.Validate(new HlsOptions()));

    [Fact]
    public void Validate_Enabled_NeedsNoStore()
        => Assert.Null(HlsOptionsValidator.Validate(new HlsOptions { Enabled = true, AllowOriginal = true }));

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 1080, 1080 })]
    [InlineData(new[] { 1080, 1000 })]
    public void Validate_Qualities_AreFromTheLadderOnceEachAndAtLeastOne(int[] qualities)
        => Assert.NotNull(HlsOptionsValidator.Validate(new HlsOptions { Qualities = qualities }));

    [Theory]
    [InlineData(0, 3, 72, 24)]
    [InlineData(61, 3, 72, 24)]
    [InlineData(5, 0, 72, 24)]
    [InlineData(5, 21, 72, 24)]
    [InlineData(5, 3, -1, 24)]
    [InlineData(5, 3, 8761, 24)]
    [InlineData(5, 3, 72, 0)]
    [InlineData(5, 3, 72, 169)]
    public void Validate_Numbers_AreInRange(int leaseMinutes, int maxAttempts, int orphanRetentionHours, int playbackTokenHours)
        => Assert.NotNull(HlsOptionsValidator.Validate(new HlsOptions
        {
            LeaseMinutes = leaseMinutes,
            MaxAttempts = maxAttempts,
            OrphanRetentionHours = orphanRetentionHours,
            PlaybackTokenHours = playbackTokenHours
        }));

    [Theory]
    [InlineData(1, 1, 0, 1)]
    [InlineData(60, 20, 8760, 168)]
    public void Validate_NumbersAtTheLimits_AreValid(int leaseMinutes, int maxAttempts, int orphanRetentionHours, int playbackTokenHours)
        => Assert.Null(HlsOptionsValidator.Validate(new HlsOptions
        {
            LeaseMinutes = leaseMinutes,
            MaxAttempts = maxAttempts,
            OrphanRetentionHours = orphanRetentionHours,
            PlaybackTokenHours = playbackTokenHours
        }));

    [Fact]
    public void Validate_Lists_AreNotNull()
    {
        Assert.NotNull(HlsOptionsValidator.Validate(new HlsOptions { Libraries = null! }));
        Assert.NotNull(HlsOptionsValidator.Validate(new HlsOptions { Qualities = null! }));
    }

    [Fact]
    public void Validate_LibraryQualities_AreFromTheLadderOnceEach_AndShareAQualityWithTheGlobalOnes()
    {
        var library = Guid.NewGuid();
        HlsOptions Options(params HlsLibraryQualities[] libraries) => new() { Qualities = [1080, 720], LibraryQualities = libraries };

        Assert.Null(HlsOptionsValidator.Validate(Options(new HlsLibraryQualities { LibraryId = library, Qualities = [720, 480] })));
        Assert.NotNull(HlsOptionsValidator.Validate(Options(new HlsLibraryQualities { LibraryId = library, Qualities = [480] })));
        Assert.NotNull(HlsOptionsValidator.Validate(Options(new HlsLibraryQualities { LibraryId = library, Qualities = [720, 720] })));
        Assert.NotNull(HlsOptionsValidator.Validate(Options(new HlsLibraryQualities { LibraryId = library, Qualities = [700] })));
        Assert.NotNull(HlsOptionsValidator.Validate(Options(new HlsLibraryQualities { LibraryId = Guid.Empty, Qualities = [720] })));
        Assert.NotNull(HlsOptionsValidator.Validate(Options(new HlsLibraryQualities { LibraryId = library, Qualities = [720] }, new HlsLibraryQualities { LibraryId = library, Qualities = [1080] })));
        Assert.NotNull(HlsOptionsValidator.Validate(Options(new HlsLibraryQualities { LibraryId = library, Qualities = null! })));
        Assert.NotNull(HlsOptionsValidator.Validate(Options(null!)));
        Assert.NotNull(HlsOptionsValidator.Validate(new HlsOptions { LibraryQualities = null! }));
        Assert.Contains(library.ToString(), HlsOptionsValidator.Validate(Options(new HlsLibraryQualities { LibraryId = library, Qualities = [480] })), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_LibraryQualities_OfALibraryNotChosen_NeedNotShareAQuality()
    {
        var library = Guid.NewGuid();
        var options = new HlsOptions { Libraries = [Guid.NewGuid()], Qualities = [1080], LibraryQualities = [new() { LibraryId = library, Qualities = [480] }] };

        Assert.Null(HlsOptionsValidator.Validate(options));
        options.Libraries = [library];
        Assert.NotNull(HlsOptionsValidator.Validate(options));
    }

    [Fact]
    public void GetQualities_AreTheGlobalOnes_NarrowedByThoseOfTheLibrary()
    {
        var narrowed = Guid.NewGuid();
        var other = Guid.NewGuid();
        var options = new HlsOptions
        {
            Enabled = true,
            Libraries = [narrowed, other],
            Qualities = [1080, 720, 480],
            LibraryQualities = [new() { LibraryId = narrowed, Qualities = [480, 720, 360] }]
        };
        var snapshot = new HlsSettingsSnapshot(options, null, 1);

        Assert.Equal([720, 480], snapshot.GetQualities(narrowed));
        Assert.Equal([1080, 720, 480], snapshot.GetQualities(other));
        Assert.Empty(snapshot.GetQualities(Guid.NewGuid()));
        Assert.Empty(new HlsSettingsSnapshot(new HlsOptions { Qualities = [1080] }, null, 1).GetQualities(other));
    }

    [Fact]
    public void Validate_SigningKey_IsBase64OfAtLeast32Bytes()
    {
        Assert.Null(HlsOptionsValidator.Validate(new HlsOptions { PlaybackSigningKey = Convert.ToBase64String(new byte[32]) }));
        Assert.NotNull(HlsOptionsValidator.Validate(new HlsOptions { PlaybackSigningKey = Convert.ToBase64String(new byte[31]) }));
        Assert.NotNull(HlsOptionsValidator.Validate(new HlsOptions { PlaybackSigningKey = "not base64!" }));
    }

    [Fact]
    public void ConfigurationStore_RefusesInvalidSettings()
    {
        var store = new HlsConfigurationStore();

        Assert.Equal("hls", store.Key);
        Assert.Equal(typeof(HlsOptions), store.ConfigurationType);
        store.Validate(new HlsOptions(), new HlsOptions { Enabled = true });
        Assert.Throws<ArgumentException>(() => store.Validate(new HlsOptions(), new HlsOptions { Enabled = true, Qualities = [] }));
    }
}
