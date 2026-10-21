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
