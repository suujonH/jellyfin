using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Net.S3;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Model.NetworkStorage;

namespace Jellyfin.Server.Implementations.NetworkStorage;

/// <summary>
/// S3 and S3-compatible storage, named by <c>.strm</c> files as <c>s3://bucket/key</c>. An access covers one bucket,
/// from its root, and is expected to be permitted to read all of it.
/// </summary>
public partial class S3StorageProtocol : INetworkStorageProtocol
{
    private static readonly IReadOnlyDictionary<string, string> _listOneObject = new Dictionary<string, string>
    {
        ["list-type"] = "2",
        ["max-keys"] = "1"
    };

    private readonly IHttpClientFactory _httpClientFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="S3StorageProtocol"/> class.
    /// </summary>
    /// <param name="httpClientFactory">The HTTP client factory.</param>
    public S3StorageProtocol(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    /// <inheritdoc />
    public string Protocol => NetworkStorageProtocols.S3;

    /// <inheritdoc />
    public bool RequiresAccess => true;

    /// <summary>
    /// Gets the S3 credentials of an access.
    /// </summary>
    /// <param name="access">The access.</param>
    /// <returns>The credentials.</returns>
    public static S3Access ToS3Access(NetworkStorageAccessDto access)
    {
        ArgumentNullException.ThrowIfNull(access);
        return new S3Access(access.Endpoint, access.Region, access.UsePathStyle, access.AccessKey, access.SecretKey, access.SessionToken);
    }

    /// <summary>
    /// Gets the signing time and lifetime of an address that stays the same for an hour, so clients and caches
    /// in front of the storage see one address for a resource instead of a new one for every request.
    /// The address is valid for at least <paramref name="seconds"/> from <paramref name="now"/>.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="seconds">How long the address should be valid.</param>
    /// <returns>When the address is signed, at the start of the hour, and its lifetime from then.</returns>
    public static (DateTime SignedAt, TimeSpan Lifetime) AlignToHour(DateTime now, int seconds)
    {
        var utc = now.ToUniversalTime();
        var hour = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
        var lifetime = TimeSpan.FromSeconds(seconds) + TimeSpan.FromHours(1);
        return (hour, lifetime < S3UrlSigner.MaxLifetime ? lifetime : S3UrlSigner.MaxLifetime);
    }

    /// <inheritdoc />
    public bool TryLocate(string target, [NotNullWhen(true)] out StorageLocation? location)
    {
        location = null;
        if (!S3Address.TryParse(target, out var address))
        {
            return false;
        }

        location = new StorageLocation(Protocol, address.Bucket, address.Key, target);
        return true;
    }

    /// <inheritdoc />
    public bool Covers(NetworkStorageAccessDto access, string container)
    {
        ArgumentNullException.ThrowIfNull(access);
        return string.Equals(access.Protocol, Protocol, StringComparison.Ordinal)
            && string.Equals(access.Bucket, container, StringComparison.Ordinal);
    }

    /// <inheritdoc />
    public Uri? GetInternalAddress(NetworkStorageAccessDto? access, StorageLocation location, DateTime now, string method)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (access is null)
        {
            return null;
        }

        return S3UrlSigner.GetUrl(ToS3Access(access), location.Container, location.ObjectPath, now, TimeSpan.FromSeconds(access.UrlLifetimeSeconds), method, null);
    }

    /// <inheritdoc />
    public Uri? GetExternalAddress(NetworkStorageAccessDto? access, NetworkStorageExternalAddressDto? external, StorageLocation location, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (access is null || external is null || !external.IsPublic)
        {
            return null;
        }

        var (signedAt, lifetime) = AlignToHour(now, external.UrlLifetimeSeconds);
        switch (external.AuthMode)
        {
            case ExternalAuthMode.PublicBucket when !string.IsNullOrEmpty(external.BaseUrl):
                return new Uri(external.BaseUrl.TrimEnd('/') + "/" + S3UrlSigner.Encode(location.ObjectPath, true));
            case ExternalAuthMode.PresignedUrl:
                var s3 = ToS3Access(access);
                if (!string.IsNullOrEmpty(external.BaseUrl))
                {
                    s3 = s3 with { Endpoint = external.BaseUrl };
                }

                return S3UrlSigner.GetUrl(s3, location.Container, location.ObjectPath, signedAt, lifetime, "GET", null);
            case ExternalAuthMode.CloudflareWorker when !string.IsNullOrEmpty(external.BaseUrl) && !string.IsNullOrEmpty(external.Secret):
                var expires = new DateTimeOffset(signedAt + lifetime).ToUnixTimeSeconds();
                return WorkerUrlSigner.GetUrl(external.BaseUrl, external.Secret, location.Container, location.ObjectPath, expires);
            default:
                return null;
        }
    }

    /// <inheritdoc />
    public async Task<NetworkStorageTestResult> TestAsync(NetworkStorageAccessDto access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!S3Address.IsValidBucket(access.Bucket))
        {
            return new NetworkStorageTestResult(false, null, "The bucket name is missing or invalid.");
        }

        if (!string.IsNullOrEmpty(access.AccessKey) && string.IsNullOrEmpty(access.SecretKey))
        {
            return new NetworkStorageTestResult(false, null, "The secret key is missing.");
        }

        Uri url;
        try
        {
            // Listing one key of the bucket root needs the permission to read the bucket, not any particular object.
            url = S3UrlSigner.GetUrl(ToS3Access(access), access.Bucket, string.Empty, DateTime.UtcNow, TimeSpan.FromMinutes(5), "GET", _listOneObject);
        }
        catch (UriFormatException)
        {
            return new NetworkStorageTestResult(false, null, "The endpoint is not a valid address.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(NetworkStorageHttp.Timeout);
        try
        {
            using var response = await _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(url, timeout.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return new NetworkStorageTestResult(true, (int)response.StatusCode, null);
            }

            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return new NetworkStorageTestResult(false, (int)response.StatusCode, GetErrorMessage(body) ?? response.ReasonPhrase);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new NetworkStorageTestResult(false, null, "The endpoint did not answer in time.");
        }
        catch (HttpRequestException ex)
        {
            return new NetworkStorageTestResult(false, null, ex.Message);
        }
    }

    /// <inheritdoc />
    public Task<ObjectCheckResult> CheckAsync(NetworkStorageAccessDto? access, StorageLocation location, long? expectedSize, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (access is null)
        {
            return Task.FromResult(new ObjectCheckResult(ObjectCheckStatus.Forbidden, null, null));
        }

        var now = DateTime.UtcNow;
        var head = S3UrlSigner.GetUrl(ToS3Access(access), location.Container, location.ObjectPath, now, TimeSpan.FromMinutes(15), "HEAD", null);
        return NetworkStorageHttp.CheckAsync(_httpClientFactory.CreateClient(NamedClient.Default), head, null, expectedSize, cancellationToken);
    }

    private static string? GetErrorMessage(string body)
    {
        // S3 answers errors with <Error><Code>AccessDenied</Code><Message>...</Message></Error>.
        var code = ErrorCodeRegex().Match(body);
        if (!code.Success)
        {
            return null;
        }

        var message = ErrorMessageRegex().Match(body);
        return message.Success ? code.Groups[1].Value + ": " + message.Groups[1].Value : code.Groups[1].Value;
    }

    [GeneratedRegex("<Code>([^<]*)</Code>")]
    private static partial Regex ErrorCodeRegex();

    [GeneratedRegex("<Message>([^<]*)</Message>")]
    private static partial Regex ErrorMessageRegex();
}
