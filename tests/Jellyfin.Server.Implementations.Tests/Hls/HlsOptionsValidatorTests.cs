using System;
using System.IO;
using Jellyfin.Server.Implementations.Hls;
using MediaBrowser.Model.Configuration;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.Hls;

public class HlsOptionsValidatorTests
{
    private static readonly string _localStore = Path.Combine(Path.GetTempPath(), "hls");

    [Fact]
    public void Validate_Defaults_AreValid()
        => Assert.Null(HlsOptionsValidator.Validate(new HlsOptions()));

    [Theory]
    [InlineData("")]
    [InlineData("s3://bucket")]
    [InlineData("s3://bucket/hls/prefix")]
    public void Validate_EnabledWithAStore_IsValid(string store)
        => Assert.Null(HlsOptionsValidator.Validate(new HlsOptions { Enabled = true, Store = store.Length == 0 ? _localStore : store }));

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] { 1080, 1080 })]
    [InlineData(new[] { 1080, 1000 })]
    public void Validate_Qualities_AreFromTheLadderOnceEachAndAtLeastOne(int[] qualities)
        => Assert.NotNull(HlsOptionsValidator.Validate(new HlsOptions { Qualities = qualities }));

    [Fact]
    public void Validate_Enabled_NeedsAStore()
        => Assert.Equal("An HLS store is required to enable pre-encoded HLS.", HlsOptionsValidator.Validate(new HlsOptions { Enabled = true }));

    [Theory]
    [InlineData("hls")]
    [InlineData("s3://")]
    [InlineData("s3:///hls")]
    [InlineData("s3://bucket/a/../b")]
    public void Validate_Store_IsAnAbsolutePathOrAPrefixOfABucket(string store)
    {
        // Checked even while off, so a store set before turning it on is valid when it is turned on.
        Assert.NotNull(HlsOptionsValidator.Validate(new HlsOptions { Store = store }));
        Assert.NotNull(HlsOptionsValidator.Validate(new HlsOptions { Enabled = true, Store = store }));
    }

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
        Assert.NotNull(HlsOptionsValidator.Validate(new HlsOptions { OriginalUsers = null! }));
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
        store.Validate(new HlsOptions(), new HlsOptions { Enabled = true, Store = _localStore });
        Assert.Throws<ArgumentException>(() => store.Validate(new HlsOptions(), new HlsOptions { Enabled = true }));
    }
}
