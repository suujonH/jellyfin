using System;
using System.Globalization;
using Jellyfin.Server.Implementations.NetworkStorage;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Model.NetworkStorage;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.NetworkStorage;

public class NetworkStorageAddressTests
{
    private static readonly DateTime _now = DateTime.Parse("2026-10-21T10:25:41Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);

    private static readonly NetworkStorageAccessDto _access = new()
    {
        Bucket = "media",
        Endpoint = "https://account.r2.cloudflarestorage.com",
        Region = "auto",
        UsePathStyle = true,
        AccessKey = "key",
        SecretKey = "secret"
    };

    private static readonly StorageLocation _location = new(NetworkStorageProtocols.S3, "media", "电影/A+B.mkv", "s3://media/电影/A+B.mkv");

    [Fact]
    public void WorkerUrlSigner_MatchesTheWorkerTestVector()
    {
        // tools/network-storage-worker/worker.test.mjs checks the same vector.
        var url = WorkerUrlSigner.GetUrl("https://media.example.com/", "shared-secret", "media", "电影/A+B.mkv", 1792576800);

        Assert.Equal(
            "https://media.example.com/media/%E7%94%B5%E5%BD%B1/A%2BB.mkv?exp=1792576800&sig=F3wrSNRrFuhGocJ1vC5yhwgZRZSia1-83EFibDTMeP0",
            url.AbsoluteUri);
    }

    [Fact]
    public void AlignToHour_GivesOneAddressPerHourValidForTheWholeLifetime()
    {
        var (signedAt, lifetime) = S3StorageProtocol.AlignToHour(_now, 3600);

        Assert.Equal(DateTime.Parse("2026-10-21T10:00:00Z", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal), signedAt);
        Assert.True(signedAt + lifetime >= _now.AddSeconds(3600));
        Assert.Equal(S3StorageProtocol.AlignToHour(_now.AddMinutes(20), 3600), (signedAt, lifetime));
    }

    [Fact]
    public void GetExternalAddress_NotPublic_IsNull()
    {
        var protocol = new S3StorageProtocol(null!);

        Assert.Null(protocol.GetExternalAddress(_access, new NetworkStorageExternalAddressDto { BaseUrl = "https://cdn.example.com" }, _location, _now));
        Assert.Null(protocol.GetExternalAddress(_access, null, _location, _now));
    }

    [Fact]
    public void GetExternalAddress_PublicBucket_IsTheKeyUnderItsAddress()
    {
        var protocol = new S3StorageProtocol(null!);
        var external = new NetworkStorageExternalAddressDto { AuthMode = ExternalAuthMode.PublicBucket, BaseUrl = "https://cdn.example.com/" };

        Assert.Equal("https://cdn.example.com/%E7%94%B5%E5%BD%B1/A%2BB.mkv", protocol.GetExternalAddress(_access, external, _location, _now)!.AbsoluteUri);
    }

    [Fact]
    public void GetExternalAddress_Presigned_UsesTheExternalEndpoint()
    {
        var protocol = new S3StorageProtocol(null!);
        var external = new NetworkStorageExternalAddressDto { AuthMode = ExternalAuthMode.PresignedUrl, BaseUrl = "https://s3.example.com", UrlLifetimeSeconds = 3600 };

        var url = protocol.GetExternalAddress(_access, external, _location, _now)!.AbsoluteUri;

        Assert.StartsWith("https://s3.example.com/media/%E7%94%B5%E5%BD%B1/A%2BB.mkv?X-Amz-Algorithm=", url, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Date=20261021T100000Z", url, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Expires=7200", url, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(200, 10L, 10L, ObjectCheckStatus.Ok)]
    [InlineData(200, 10L, null, ObjectCheckStatus.Ok)]
    [InlineData(200, 9L, 10L, ObjectCheckStatus.SizeMismatch)]
    [InlineData(404, null, 10L, ObjectCheckStatus.NotFound)]
    [InlineData(410, null, null, ObjectCheckStatus.NotFound)]
    [InlineData(403, null, null, ObjectCheckStatus.Forbidden)]
    [InlineData(429, null, null, ObjectCheckStatus.Transient)]
    [InlineData(503, null, null, ObjectCheckStatus.Transient)]
    public void ObjectCheckResult_FromStatus_SeparatesResourceFailuresFromTransientOnes(int status, long? size, long? expected, ObjectCheckStatus result)
    {
        Assert.Equal(result, ObjectCheckResult.FromStatus(status, size, expected).Status);
    }

    [Theory]
    [InlineData(false, false, false, false, null, NetworkStorageResourceState.Unknown, NetworkStorageIssue.Unresolved)]
    [InlineData(true, true, false, true, null, NetworkStorageResourceState.Invalid, NetworkStorageIssue.NoAccess)]
    [InlineData(true, true, true, true, NetworkStorageIssue.NotFound, NetworkStorageResourceState.Invalid, NetworkStorageIssue.NotFound)]
    [InlineData(true, true, true, false, null, NetworkStorageResourceState.Valid, null)]
    [InlineData(true, false, false, false, null, NetworkStorageResourceState.Unknown, NetworkStorageIssue.Undeclared)]
    [InlineData(true, false, false, true, null, NetworkStorageResourceState.Valid, null)]
    public void Classify_TellsTheStateFromWhatIsKnown(
        bool located,
        bool requiresAccess,
        bool hasAccess,
        bool hasTrueCache,
        NetworkStorageIssue? failure,
        NetworkStorageResourceState state,
        NetworkStorageIssue? issue)
    {
        Assert.Equal((state, issue), NetworkStorageClassifier.Classify(located, requiresAccess, hasAccess, hasTrueCache, failure));
    }
}
