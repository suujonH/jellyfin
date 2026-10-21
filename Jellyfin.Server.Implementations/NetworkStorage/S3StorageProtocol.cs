using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using MediaBrowser.Common.Net;
using MediaBrowser.Common.Net.S3;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Model.NetworkStorage;

namespace Jellyfin.Server.Implementations.NetworkStorage;

/// <summary>
/// S3 and S3-compatible storage, named by <c>.strm</c> files as <c>s3://bucket/key</c>. An access covers one bucket,
/// from its root, and is expected to be permitted to read all of it; to be the HLS store, also to write and delete in it.
/// </summary>
public partial class S3StorageProtocol : IObjectStorageProtocol
{
    // The most keys S3 lists in a page or deletes in a request.
    private const int MaxKeysPerRequest = 1000;

    private static readonly IReadOnlyDictionary<string, string> _listOneObject = new Dictionary<string, string>
    {
        ["list-type"] = "2",
        ["max-keys"] = "1"
    };

    private static readonly IReadOnlyDictionary<string, string> _deleteObjects = new Dictionary<string, string>
    {
        ["delete"] = string.Empty
    };

    // The lifetime of the addresses of the requests the server sends itself, right away.
    private static readonly TimeSpan _requestLifetime = TimeSpan.FromMinutes(15);

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
    public TimeSpan GetExternalAddressLifetime(NetworkStorageExternalAddressDto? external)
    {
        // An address of a public bucket is not signed, so it never expires.
        if (external is not { IsPublic: true } || external.AuthMode is not (ExternalAuthMode.PresignedUrl or ExternalAuthMode.CloudflareWorker))
        {
            return TimeSpan.Zero;
        }

        // Signed at the start of the hour, so valid until the lifetime after it at most.
        return AlignToHour(DateTime.UtcNow, external.UrlLifetimeSeconds).Lifetime;
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<StorageObject>> ListAsync(NetworkStorageAccessDto access, StorageLocation prefix, int? limit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(prefix);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit ?? 1, 1, nameof(limit));

        var what = "Listing " + prefix.Target;
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        var max = limit ?? int.MaxValue;
        var objects = new List<StorageObject>();
        string? continuationToken = null;
        do
        {
            var parameters = new Dictionary<string, string>
            {
                ["list-type"] = "2",
                ["prefix"] = prefix.ObjectPath,
                ["max-keys"] = Math.Min(MaxKeysPerRequest, max - objects.Count).ToString(CultureInfo.InvariantCulture)
            };
            if (continuationToken is not null)
            {
                parameters["continuation-token"] = continuationToken;
            }

            var url = S3UrlSigner.GetUrl(ToS3Access(access), prefix.Container, string.Empty, DateTime.UtcNow, _requestLifetime, "GET", parameters);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var (status, body) = await NetworkStorageHttp.SendAsync(client, request, what, cancellationToken).ConfigureAwait(false);
            if (!NetworkStorageHttp.IsSuccess(status))
            {
                throw CreateError(what, status, body);
            }

            try
            {
                var page = S3Xml.ReadListPage(body);
                objects.AddRange(page.Objects);
                continuationToken = page.NextContinuationToken;
            }
            catch (Exception ex) when (ex is XmlException or FormatException or OverflowException)
            {
                throw new IOException(what + " failed: the storage answered no listing: " + ex.Message, ex);
            }
        }
        while (continuationToken is not null && objects.Count < max);

        if (objects.Count > max)
        {
            objects.RemoveRange(max, objects.Count - max);
        }

        return objects;
    }

    /// <inheritdoc />
    public async Task DeleteAsync(NetworkStorageAccessDto access, string container, IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        ArgumentException.ThrowIfNullOrEmpty(container);
        ArgumentNullException.ThrowIfNull(keys);

        var s3 = ToS3Access(access);
        var client = _httpClientFactory.CreateClient(NamedClient.Default);
        var deletesBatches = true;
        foreach (var batch in keys.Chunk(MaxKeysPerRequest))
        {
            if (deletesBatches)
            {
                deletesBatches = await DeleteBatchAsync(client, s3, container, batch, cancellationToken).ConfigureAwait(false);
                if (deletesBatches)
                {
                    continue;
                }
            }

            foreach (var key in batch)
            {
                await DeleteObjectAsync(client, s3, container, key, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Gets the error of a request the storage refused.
    /// </summary>
    /// <param name="what">What the request does.</param>
    /// <param name="status">The status of the answer.</param>
    /// <param name="body">The body of the answer.</param>
    /// <returns>The error, with the code and message S3 answered.</returns>
    internal static IOException CreateError(string what, HttpStatusCode status, byte[] body)
        => new(what + " failed: " + ((int)status).ToString(CultureInfo.InvariantCulture) + " " + (GetErrorMessage(Encoding.UTF8.GetString(body)) ?? status.ToString()));

    // Deletes up to a thousand objects with one DeleteObjects request; false when the storage does not take it.
    private static async Task<bool> DeleteBatchAsync(HttpClient client, S3Access access, string container, string[] keys, CancellationToken cancellationToken)
    {
        var what = "Deleting " + keys.Length.ToString(CultureInfo.InvariantCulture) + " objects of " + S3Address.Prefix + container;
        var content = S3Xml.WriteDelete(keys);
        var url = S3UrlSigner.GetUrl(access, container, string.Empty, DateTime.UtcNow, _requestLifetime, "POST", _deleteObjects);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new ByteArrayContent(content) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");

        // CA5351: Do Not Use Broken Cryptographic Algorithms
#pragma warning disable CA5351
        // S3 requires the digest of the body; it is not used for security.
        request.Content.Headers.ContentMD5 = MD5.HashData(content);
#pragma warning restore CA5351

        var (status, body) = await NetworkStorageHttp.SendAsync(client, request, what, cancellationToken).ConfigureAwait(false);

        // A service that does not take DeleteObjects, or not with a presigned address, refuses it in one of these ways;
        // deleting each object then tells whether deleting is permitted at all.
        if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented)
        {
            return false;
        }

        if (!NetworkStorageHttp.IsSuccess(status))
        {
            throw CreateError(what, status, body);
        }

        IReadOnlyList<(string Key, string Code, string Message)> errors;
        try
        {
            errors = S3Xml.ReadDeleteErrors(body);
        }
        catch (Exception ex) when (ex is XmlException or FormatException)
        {
            throw new IOException(what + " failed: the storage answered no result: " + ex.Message, ex);
        }

        // An object that does not exist is deleted already.
        foreach (var (key, code, message) in errors)
        {
            if (!string.Equals(code, "NoSuchKey", StringComparison.Ordinal))
            {
                throw new IOException("Deleting " + S3Address.Prefix + container + "/" + key + " failed: " + code + ": " + message);
            }
        }

        return true;
    }

    private static async Task DeleteObjectAsync(HttpClient client, S3Access access, string container, string key, CancellationToken cancellationToken)
    {
        var what = "Deleting " + S3Address.Prefix + container + "/" + key;
        var url = S3UrlSigner.GetUrl(access, container, key, DateTime.UtcNow, _requestLifetime, "DELETE", null);
        using var request = new HttpRequestMessage(HttpMethod.Delete, url);
        var (status, body) = await NetworkStorageHttp.SendAsync(client, request, what, cancellationToken).ConfigureAwait(false);
        if (!NetworkStorageHttp.IsSuccess(status) && status != HttpStatusCode.NotFound)
        {
            throw CreateError(what, status, body);
        }
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
