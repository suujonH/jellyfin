using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Extensions;
using MediaBrowser.Common.Net.S3;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Controller.TrueCache;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.MediaInfo;
using MediaBrowser.Model.NetworkStorage;
using MediaBrowser.Model.Querying;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Audio = MediaBrowser.Controller.Entities.Audio.Audio;

namespace Jellyfin.Server.Implementations.NetworkStorage;

/// <summary>
/// The network storage of the library, kept in the database. Accesses are held in memory, since every source of a
/// <c>.strm</c> item is resolved through them.
/// </summary>
public sealed class NetworkStorageManager : INetworkStorageManager, IDisposable
{
    /// <summary>
    /// The key of the resources whose target belongs to no known protocol or is not known yet.
    /// </summary>
    public const string UnresolvedStorageKey = "unresolved";

    private const int MinLifetimeSeconds = 60;
    private const int CheckParallelism = 4;

    private static readonly TimeSpan _reportInterval = TimeSpan.FromMinutes(5);

    // Item types that are one resource each: movies, episodes and tracks, not seasons or other folders.
    private static readonly string[] _resourceTypes =
    [
        typeof(Movie).FullName!,
        typeof(Episode).FullName!,
        typeof(Video).FullName!,
        typeof(MusicVideo).FullName!,
        typeof(Trailer).FullName!,
        typeof(Audio).FullName!,
        typeof(AudioBook).FullName!
    ];

    private readonly IDbContextFactory<JellyfinDbContext> _dbProvider;
    private readonly IReadOnlyList<INetworkStorageProtocol> _protocols;
    private readonly ITrueCacheService _trueCache;
    private readonly ILogger<NetworkStorageManager> _logger;
    private readonly ConcurrentDictionary<Guid, DateTime> _lastReported = new();
    private readonly Lock _accessesLock = new();

    // Writes of failures, so that a check and the reports that come in while it runs do not insert the same item twice.
    private readonly SemaphoreSlim _failureWriteLock = new(1, 1);
    private IReadOnlyList<StoredAccess>? _accesses;

    /// <summary>
    /// Initializes a new instance of the <see cref="NetworkStorageManager"/> class.
    /// </summary>
    /// <param name="dbProvider">The database context factory.</param>
    /// <param name="protocols">The protocols.</param>
    /// <param name="trueCache">The <c>__true_cache__</c> lookup.</param>
    /// <param name="logger">The logger.</param>
    public NetworkStorageManager(
        IDbContextFactory<JellyfinDbContext> dbProvider,
        IEnumerable<INetworkStorageProtocol> protocols,
        ITrueCacheService trueCache,
        ILogger<NetworkStorageManager> logger)
    {
        _dbProvider = dbProvider;
        _protocols = protocols.ToArray();
        _trueCache = trueCache;
        _logger = logger;
    }

    private IReadOnlyList<StoredAccess> Accesses
    {
        get
        {
            var accesses = _accesses;
            if (accesses is not null)
            {
                return accesses;
            }

            lock (_accessesLock)
            {
                return _accesses ??= LoadAccesses();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose() => _failureWriteLock.Dispose();

    /// <inheritdoc />
    public IReadOnlyList<NetworkStorageAccessDto> GetAccesses() => Accesses.Select(a => a.Access).ToArray();

    /// <inheritdoc />
    public NetworkStorageAccessDto? GetAccess(Guid id) => Accesses.FirstOrDefault(a => a.Access.Id.Equals(id))?.Access;

    /// <inheritdoc />
    public async Task<NetworkStorageAccessDto> SaveAccessAsync(NetworkStorageAccessDto access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        if (!_protocols.Any(p => p.RequiresAccess && string.Equals(p.Protocol, access.Protocol, StringComparison.Ordinal)))
        {
            throw new ArgumentException("Accesses are not used with protocol " + access.Protocol + ".", nameof(access));
        }

        ValidateAccess(access);

        var context = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            if (await context.NetworkStorageAccesses.AnyAsync(e => e.Protocol == access.Protocol && e.Bucket == access.Bucket && !e.Id.Equals(access.Id), cancellationToken).ConfigureAwait(false))
            {
                throw new ArgumentException("Another access already covers " + access.Bucket + ".", nameof(access));
            }

            var now = DateTime.UtcNow;
            NetworkStorageAccess entity;
            if (access.Id.Equals(Guid.Empty))
            {
                entity = new NetworkStorageAccess { Id = Guid.NewGuid(), Protocol = access.Protocol, Bucket = access.Bucket, DateCreated = now };
                context.NetworkStorageAccesses.Add(entity);
            }
            else
            {
                entity = await context.NetworkStorageAccesses.FirstOrDefaultAsync(e => e.Id.Equals(access.Id), cancellationToken).ConfigureAwait(false)
                    ?? throw new ResourceNotFoundException("No access has id " + access.Id + ".");
            }

            entity.Protocol = access.Protocol;
            entity.Name = string.IsNullOrWhiteSpace(access.Name) ? null : access.Name.Trim();
            entity.Endpoint = string.IsNullOrWhiteSpace(access.Endpoint) ? null : access.Endpoint.Trim();
            entity.Bucket = access.Bucket;
            entity.Region = string.IsNullOrWhiteSpace(access.Region) ? null : access.Region.Trim();
            entity.UsePathStyle = access.UsePathStyle;
            entity.AccessKey = string.IsNullOrWhiteSpace(access.AccessKey) ? null : access.AccessKey.Trim();
            entity.SecretKey = MergeSecret(entity.SecretKey, access.SecretKey);
            entity.SessionToken = MergeSecret(entity.SessionToken, access.SessionToken);
            entity.UrlLifetimeSeconds = Math.Clamp(access.UrlLifetimeSeconds, MinLifetimeSeconds, (int)S3UrlSigner.MaxLifetime.TotalSeconds);
            entity.IsArchive = access.IsArchive;
            entity.Settings = string.IsNullOrWhiteSpace(access.Settings) ? null : access.Settings;
            entity.DateModified = now;

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            ReloadAccesses();
            return GetAccess(entity.Id)!;
        }
    }

    /// <inheritdoc />
    public async Task<bool> DeleteAccessAsync(Guid id, CancellationToken cancellationToken)
    {
        var context = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var deleted = await context.NetworkStorageAccesses.Where(e => e.Id.Equals(id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            ReloadAccesses();
            return deleted > 0;
        }
    }

    /// <inheritdoc />
    public Task<NetworkStorageTestResult> TestAccessAsync(NetworkStorageAccessDto access, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(access);
        var protocol = _protocols.FirstOrDefault(p => string.Equals(p.Protocol, access.Protocol, StringComparison.Ordinal));
        if (protocol is null)
        {
            return Task.FromResult(new NetworkStorageTestResult(false, null, "Unknown protocol " + access.Protocol + "."));
        }

        var stored = access.Id.Equals(Guid.Empty) ? null : GetAccess(access.Id);
        var tested = new NetworkStorageAccessDto
        {
            Id = access.Id,
            Protocol = access.Protocol,
            Endpoint = string.IsNullOrWhiteSpace(access.Endpoint) ? null : access.Endpoint.Trim(),
            Bucket = access.Bucket?.Trim() ?? string.Empty,
            Region = string.IsNullOrWhiteSpace(access.Region) ? null : access.Region.Trim(),
            UsePathStyle = access.UsePathStyle,
            AccessKey = string.IsNullOrWhiteSpace(access.AccessKey) ? null : access.AccessKey.Trim(),
            SecretKey = MergeSecret(stored?.SecretKey, access.SecretKey),
            SessionToken = MergeSecret(stored?.SessionToken, access.SessionToken),
            UrlLifetimeSeconds = access.UrlLifetimeSeconds
        };

        return protocol.TestAsync(tested, cancellationToken);
    }

    /// <inheritdoc />
    public NetworkStorageExternalAddressDto GetExternalAddress(Guid accessId)
        => Accesses.FirstOrDefault(a => a.Access.Id.Equals(accessId))?.External ?? new NetworkStorageExternalAddressDto { AccessId = accessId };

    /// <inheritdoc />
    public async Task<NetworkStorageExternalAddressDto> SaveExternalAddressAsync(NetworkStorageExternalAddressDto external, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(external);
        var access = GetAccess(external.AccessId) ?? throw new ResourceNotFoundException("No access has id " + external.AccessId + ".");

        var context = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var entity = await context.NetworkStorageExternalAddresses.FirstOrDefaultAsync(e => e.AccessId.Equals(access.Id), cancellationToken).ConfigureAwait(false);
            if (entity is null)
            {
                entity = new NetworkStorageExternalAddress { AccessId = access.Id };
                context.NetworkStorageExternalAddresses.Add(entity);
            }

            var secret = MergeSecret(entity.Secret, external.Secret);
            var baseUrl = string.IsNullOrWhiteSpace(external.BaseUrl) ? null : external.BaseUrl.Trim();
            ValidateExternalAddress(access, external, baseUrl, secret);

            // Setting an external address is what makes the storage public.
            entity.IsPublic = external.AuthMode.HasValue;
            entity.AuthMode = external.AuthMode is { } mode ? (int)mode : null;
            entity.BaseUrl = baseUrl;
            entity.Secret = secret;
            entity.UrlLifetimeSeconds = Math.Clamp(external.UrlLifetimeSeconds, MinLifetimeSeconds, (int)S3UrlSigner.MaxLifetime.TotalSeconds);

            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            ReloadAccesses();
            return GetExternalAddress(access.Id);
        }
    }

    /// <inheritdoc />
    public string? ResolveShortcutTarget(string target)
    {
        if (!TryLocate(target, out var location, out var protocol))
        {
            // A malformed s3:// address cannot be read; any other target, such as rtsp, is not network storage.
            return S3Address.IsS3Path(target) ? null : target;
        }

        var access = FindAccess(protocol, location.Container);
        if (protocol.RequiresAccess && access is null)
        {
            return null;
        }

        // A storage published at an external address is read there by the server too, as clients read it;
        // only maintenance, such as checks and tests, goes to the endpoint of the access.
        var now = DateTime.UtcNow;
        return (protocol.GetExternalAddress(access?.Access, access?.External, location, now)
            ?? protocol.GetInternalAddress(access?.Access, location, now, "GET"))?.AbsoluteUri;
    }

    /// <inheritdoc />
    public Uri? GetClientAddress(string target)
    {
        if (!TryLocate(target, out var location, out var protocol))
        {
            return null;
        }

        // Clients get no address of an archive, as PrepareForClient gives them none.
        var access = FindAccess(protocol, location.Container);
        if ((protocol.RequiresAccess && access is null) || access?.Access.IsArchive == true)
        {
            return null;
        }

        return protocol.GetExternalAddress(access?.Access, access?.External, location, DateTime.UtcNow);
    }

    /// <inheritdoc />
    public Uri? GetServerAddress(string target, string method)
    {
        if (!TryLocate(target, out var location, out var protocol))
        {
            return null;
        }

        var access = FindAccess(protocol, location.Container);
        return protocol.RequiresAccess && access is null ? null : protocol.GetInternalAddress(access?.Access, location, DateTime.UtcNow, method);
    }

    /// <inheritdoc />
    public Uri? GetUploadAddress(string target, TimeSpan lifetime)
        => TryLocate(target, out var location, out var protocol)
            && protocol is IObjectStorageProtocol objects
            && FindAccess(protocol, location.Container) is { } access
                ? objects.GetUploadAddress(access.Access, location, DateTime.UtcNow, lifetime)
                : null;

    /// <inheritdoc />
    public async Task<IReadOnlyList<StorageObject>> ListAsync(string prefix, int? limit, CancellationToken cancellationToken)
    {
        var (location, protocol, access) = LocateWritable(prefix);
        return await protocol.ListAsync(access, location, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task DeleteAsync(IReadOnlyCollection<string> targets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var objects = targets.Select(LocateWritable).ToArray();
        foreach (var storage in objects.GroupBy(o => o.Location.StorageKey, StringComparer.Ordinal))
        {
            var (location, protocol, access) = storage.First();
            var keys = storage.Select(o => o.Location.ObjectPath).ToArray();
            await protocol.DeleteAsync(access, location.Container, keys, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public void PrepareForClient(MediaSourceInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (string.IsNullOrEmpty(source.ShortcutTarget)
            || !TryLocate(source.ShortcutTarget, out var location, out var protocol)
            || !protocol.RequiresAccess)
        {
            // Without an access the address is the one the .strm file names, which carries nothing of the server's.
            return;
        }

        var access = FindAccess(protocol, location.Container);
        if (access?.Access.IsArchive == true)
        {
            // An archive is only kept: the client gets no address of it, and the server will neither stream
            // nor transcode it.
            source.Path = source.ShortcutTarget;
            source.SupportsDirectPlay = false;
            source.SupportsDirectStream = false;
            source.SupportsTranscoding = false;
            return;
        }

        var address = protocol.GetExternalAddress(access?.Access, access?.External, location, DateTime.UtcNow);
        if (address is null)
        {
            // Not public: the client gets no address of the storage. Like a local file, the source is offered as
            // a file of the server, so a client that can play it directly gets it from the static stream, which the
            // server proxies, instead of having it transcoded.
            source.Path = source.ShortcutTarget;
            source.Protocol = MediaProtocol.File;
        }
        else
        {
            source.Path = address.AbsoluteUri;
        }
    }

    /// <inheritdoc />
    public bool IsArchived(string? target)
        => !string.IsNullOrEmpty(target)
            && TryLocate(target, out var location, out var protocol)
            && FindAccess(protocol, location.Container)?.Access.IsArchive == true;

    /// <inheritdoc />
    public async Task<IReadOnlyList<NetworkStorageSummary>> GetStoragesAsync(CancellationToken cancellationToken)
    {
        var resources = await GetAllResourcesAsync(cancellationToken).ConfigureAwait(false);
        var summaries = new Dictionary<string, NetworkStorageSummary>(StringComparer.Ordinal);

        // Every access is listed, with or without resources.
        foreach (var stored in Accesses)
        {
            var key = StorageLocation.GetStorageKey(stored.Access.Protocol, stored.Access.Bucket);
            summaries[key] = new NetworkStorageSummary
            {
                Key = key,
                Protocol = stored.Access.Protocol,
                Name = stored.Access.Bucket,
                AccessId = stored.Access.Id,
                IsArchive = stored.Access.IsArchive,
                RequiresAccess = true
            };
        }

        foreach (var resource in resources)
        {
            if (!summaries.TryGetValue(resource.StorageKey, out var summary))
            {
                summary = new NetworkStorageSummary
                {
                    Key = resource.StorageKey,
                    Protocol = resource.ProtocolName,
                    Name = resource.Container,
                    RequiresAccess = resource.RequiresAccess
                };
                summaries[resource.StorageKey] = summary;
            }

            summary.ResourceCount++;
            switch (resource.Resource.State)
            {
                case NetworkStorageResourceState.Valid:
                    summary.ValidCount++;
                    break;
                case NetworkStorageResourceState.Invalid:
                    summary.InvalidCount++;
                    break;
                default:
                    summary.UnknownCount++;
                    break;
            }

            if (resource.Resource.Size is { } size)
            {
                summary.TotalSize += size;
            }
            else
            {
                summary.UndeclaredSizeCount++;
            }
        }

        return summaries.Values
            .OrderBy(s => s.Key == UnresolvedStorageKey)
            .ThenBy(s => s.Protocol, StringComparer.Ordinal)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <inheritdoc />
    public async Task<QueryResult<NetworkStorageResource>> GetResourcesAsync(
        string storageKey,
        IReadOnlyCollection<NetworkStorageResourceState> states,
        int startIndex,
        int? limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(states);
        var resources = await GetAllResourcesAsync(cancellationToken).ConfigureAwait(false);
        var matching = resources
            .Where(r => string.Equals(r.StorageKey, storageKey, StringComparison.Ordinal))
            .Select(r => r.Resource)
            .Where(r => states.Count == 0 || states.Contains(r.State))
            .OrderBy(r => r.State switch
            {
                NetworkStorageResourceState.Invalid => 0,
                NetworkStorageResourceState.Unknown => 1,
                _ => 2
            })
            .ThenBy(r => r.SeriesName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        IEnumerable<NetworkStorageResource> page = matching.Skip(Math.Max(0, startIndex));
        if (limit.HasValue)
        {
            page = page.Take(Math.Max(0, limit.Value));
        }

        return new QueryResult<NetworkStorageResource>(startIndex, matching.Length, page.ToArray());
    }

    /// <inheritdoc />
    public async Task ReportAccessFailureAsync(Guid itemId, NetworkStorageFailureSource source, string? target, CancellationToken cancellationToken)
    {
        // A failing stream is reported for each request it gets; check the resource once in a while only.
        var now = DateTime.UtcNow;
        if (_lastReported.TryGetValue(itemId, out var last) && now - last < _reportInterval)
        {
            return;
        }

        _lastReported[itemId] = now;
        try
        {
            var context = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                var row = (await LoadRowsAsync(context.BaseItems.Where(e => e.Id.Equals(itemId)), cancellationToken).ConfigureAwait(false)).FirstOrDefault();
                if (row is not null && !string.IsNullOrEmpty(target))
                {
                    row = row with { Target = target };
                }

                var resource = row is null ? null : Describe(row, null);
                if (resource?.Location is null || resource.ProtocolHandler is null)
                {
                    return;
                }

                var access = FindAccess(resource.ProtocolHandler, resource.Location.Container);
                if (resource.ProtocolHandler.RequiresAccess && access is null)
                {
                    // Already invalid for having no access.
                    return;
                }

                var result = await resource.ProtocolHandler.CheckAsync(access?.Access, resource.Location, resource.Resource.Size, cancellationToken).ConfigureAwait(false);
                if (NetworkStorageClassifier.ToIssue(result.Status) is not { } issue)
                {
                    _logger.LogDebug("Reading {Target} failed, but it answers {Status}; not recorded", resource.Location.Target, result.Status);
                    return;
                }

                _logger.LogWarning("{Target} named by {Path} cannot be read: {Issue} ({StatusCode})", resource.Location.Target, row!.Path, issue, result.StatusCode);
                await RecordFailureAsync(context, itemId, resource.Location, issue, result.StatusCode, source, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Failed to check the network resource of item {ItemId}", itemId);
        }
    }

    /// <inheritdoc />
    public async Task CheckAllAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        var resources = await GetAllResourcesAsync(cancellationToken).ConfigureAwait(false);
        var checkable = resources
            .Where(r => r.Location is not null && r.ProtocolHandler is not null && (!r.RequiresAccess || r.Access is not null))
            .ToArray();

        var context = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            await context.NetworkStorageFailures.ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
        }

        var failures = new ConcurrentBag<NetworkStorageFailure>();
        var done = 0;
        await Parallel.ForEachAsync(
            checkable,
            new ParallelOptions { MaxDegreeOfParallelism = CheckParallelism, CancellationToken = cancellationToken },
            async (resource, token) =>
            {
                var result = await resource.ProtocolHandler!.CheckAsync(resource.Access?.Access, resource.Location!, resource.Resource.Size, token).ConfigureAwait(false);
                if (NetworkStorageClassifier.ToIssue(result.Status) is { } issue)
                {
                    var now = DateTime.UtcNow;
                    failures.Add(new NetworkStorageFailure
                    {
                        ItemId = resource.Resource.ItemId,
                        StorageKey = resource.StorageKey,
                        Target = resource.Location!.Target,
                        Issue = (int)issue,
                        StatusCode = result.StatusCode,
                        Source = (int)NetworkStorageFailureSource.Check,
                        DateFirstFailed = now,
                        DateLastFailed = now,
                        Count = 1
                    });
                }
                else if (result.Status == ObjectCheckStatus.Transient)
                {
                    _logger.LogInformation("{Target} could not be checked: the storage answered {StatusCode}", resource.Location!.Target, result.StatusCode);
                }

                progress.Report(100.0 * Interlocked.Increment(ref done) / checkable.Length);
            }).ConfigureAwait(false);

        await SaveCheckFailuresAsync(failures.ToArray(), cancellationToken).ConfigureAwait(false);

        _lastReported.Clear();
        _logger.LogInformation("Checked {Count} network resources, {Failed} cannot be read", checkable.Length, failures.Count);
        progress.Report(100);
    }

    private static string? MergeSecret(string? stored, string? requested)
        => requested is null ? stored : (string.IsNullOrWhiteSpace(requested) ? null : requested.Trim());

    private static void ValidateAccess(NetworkStorageAccessDto access)
    {
        access.Bucket = access.Bucket?.Trim() ?? string.Empty;
        if (!S3Address.IsValidBucket(access.Bucket))
        {
            throw new ArgumentException("The bucket name is missing or invalid.", nameof(access));
        }

        if (!string.IsNullOrWhiteSpace(access.Endpoint)
            && (!Uri.TryCreate(access.Endpoint.Trim(), UriKind.Absolute, out var endpoint)
                || (endpoint.Scheme != Uri.UriSchemeHttp && endpoint.Scheme != Uri.UriSchemeHttps)))
        {
            throw new ArgumentException("The endpoint must be an http or https address.", nameof(access));
        }
    }

    private static void ValidateExternalAddress(NetworkStorageAccessDto access, NetworkStorageExternalAddressDto external, string? baseUrl, string? secret)
    {
        if (baseUrl is not null
            && (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)))
        {
            throw new ArgumentException("The address must be an http or https address.", nameof(external));
        }

        switch (external.AuthMode)
        {
            case ExternalAuthMode.CloudflareWorker when baseUrl is null || secret is null:
                throw new ArgumentException("A Cloudflare Worker needs its address and the secret it shares with the server.", nameof(external));
            case ExternalAuthMode.PublicBucket when baseUrl is null:
                throw new ArgumentException("A public bucket needs the address it is read at.", nameof(external));
            case ExternalAuthMode.PresignedUrl when string.IsNullOrEmpty(access.AccessKey) || string.IsNullOrEmpty(access.SecretKey):
                throw new ArgumentException("Presigned addresses need an access with an access key and a secret key.", nameof(external));
        }
    }

    private static async Task<List<ResourceRow>> LoadRowsAsync(IQueryable<BaseItemEntity> query, CancellationToken cancellationToken)
    {
        var rows = await query
            .Where(e => !e.IsVirtualItem && e.Path != null && EF.Functions.Like(e.Path, "%.strm") && _resourceTypes.Contains(e.Type))
            .Select(e => new { e.Id, e.Name, e.SeriesName, e.Type, e.Path, e.Size, e.Data })
            .AsNoTracking()
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return rows.Select(r => new ResourceRow(r.Id, r.Name, r.SeriesName, r.Type, r.Path!, r.Size, ReadShortcutPath(r.Data))).ToList();
    }

    private static string? ReadShortcutPath(string? data)
    {
        // The target is only kept in the serialized item, written once the item is probed.
        if (string.IsNullOrEmpty(data))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(data);
            return document.RootElement.TryGetProperty(nameof(BaseItem.ShortcutPath), out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private bool TryLocate(string target, out StorageLocation location, out INetworkStorageProtocol protocol)
    {
        foreach (var candidate in _protocols)
        {
            if (candidate.TryLocate(target, out var found))
            {
                location = found;
                protocol = candidate;
                return true;
            }
        }

        location = null!;
        protocol = null!;
        return false;
    }

    private StoredAccess? FindAccess(INetworkStorageProtocol protocol, string container)
        => protocol.RequiresAccess ? Accesses.FirstOrDefault(a => protocol.Covers(a.Access, container)) : null;

    // The storage the server can write a target is in, and the access that covers it.
    private (StorageLocation Location, IObjectStorageProtocol Protocol, NetworkStorageAccessDto Access) LocateWritable(string target)
    {
        if (!TryLocate(target, out var location, out var protocol) || protocol is not IObjectStorageProtocol objects)
        {
            throw new ArgumentException(target + " is not in storage the server can write.", nameof(target));
        }

        var access = FindAccess(protocol, location.Container) ?? throw new IOException("No access covers " + location.StorageKey + ".");
        return (location, objects, access.Access);
    }

    private void ReloadAccesses()
    {
        lock (_accessesLock)
        {
            _accesses = LoadAccesses();
        }
    }

    private List<StoredAccess> LoadAccesses()
    {
        using var context = _dbProvider.CreateDbContext();
        return context.NetworkStorageAccesses
            .AsNoTracking()
            .Include(e => e.ExternalAddress)
            .OrderBy(e => e.Bucket)
            .AsEnumerable()
            .Select(e => new StoredAccess(
                new NetworkStorageAccessDto
                {
                    Id = e.Id,
                    Protocol = e.Protocol,
                    Name = e.Name,
                    Endpoint = e.Endpoint,
                    Bucket = e.Bucket,
                    Region = e.Region,
                    UsePathStyle = e.UsePathStyle,
                    AccessKey = e.AccessKey,
                    SecretKey = e.SecretKey,
                    SessionToken = e.SessionToken,
                    HasSecretKey = !string.IsNullOrEmpty(e.SecretKey),
                    HasSessionToken = !string.IsNullOrEmpty(e.SessionToken),
                    UrlLifetimeSeconds = e.UrlLifetimeSeconds,
                    IsArchive = e.IsArchive,
                    Settings = e.Settings
                },
                e.ExternalAddress is null
                    ? new NetworkStorageExternalAddressDto { AccessId = e.Id }
                    : new NetworkStorageExternalAddressDto
                    {
                        AccessId = e.Id,
                        // A storage made private before the address alone decided it keeps no address.
                        AuthMode = e.ExternalAddress.IsPublic && e.ExternalAddress.AuthMode is { } mode ? (ExternalAuthMode)mode : null,
                        BaseUrl = e.ExternalAddress.BaseUrl,
                        Secret = e.ExternalAddress.Secret,
                        HasSecret = !string.IsNullOrEmpty(e.ExternalAddress.Secret),
                        UrlLifetimeSeconds = e.ExternalAddress.UrlLifetimeSeconds
                    }))
            .ToList();
    }

    private async Task<IReadOnlyList<DescribedResource>> GetAllResourcesAsync(CancellationToken cancellationToken)
    {
        var context = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await using (context.ConfigureAwait(false))
        {
            var rows = await LoadRowsAsync(context.BaseItems, cancellationToken).ConfigureAwait(false);
            var failures = await context.NetworkStorageFailures.AsNoTracking().ToDictionaryAsync(f => f.ItemId, cancellationToken).ConfigureAwait(false);
            return rows.Select(row => Describe(row, failures.GetValueOrDefault(row.Id))).ToArray();
        }
    }

    private DescribedResource Describe(ResourceRow row, NetworkStorageFailure? failure)
    {
        // The size of a declared .strm is the size of its media, read from the declaration: the stored size
        // stays the size of the text file until the item is refreshed.
        var lookup = _trueCache.Lookup(row.Path);
        var hasTrueCache = lookup.IsValid;
        StorageLocation? location = null;
        INetworkStorageProtocol? protocol = null;
        if (row.Target is not null && TryLocate(row.Target, out var found, out var handler))
        {
            location = found;
            protocol = handler;
        }

        // A failure of another target the .strm named before says nothing of the current one.
        if (failure is not null && !string.Equals(failure.Target, location?.Target, StringComparison.Ordinal))
        {
            failure = null;
        }

        var access = protocol is null || location is null ? null : FindAccess(protocol, location.Container);
        var (state, issue) = NetworkStorageClassifier.Classify(
            location is not null,
            protocol?.RequiresAccess ?? false,
            access is not null,
            hasTrueCache,
            failure is null ? null : (NetworkStorageIssue)failure.Issue);

        var resource = new NetworkStorageResource
        {
            ItemId = row.Id,
            Name = row.Name,
            SeriesName = row.SeriesName,
            Type = row.Type[(row.Type.LastIndexOf('.') + 1)..],
            Path = row.Path,
            Target = row.Target,
            Size = hasTrueCache ? lookup.Entry!.MediaSize : null,
            HasTrueCache = hasTrueCache,
            State = state,
            Issue = issue,
            StatusCode = failure?.StatusCode,
            FailureSource = failure is null ? null : (NetworkStorageFailureSource)failure.Source,
            DateLastFailed = failure?.DateLastFailed,
            FailureCount = failure?.Count ?? 0
        };

        return new DescribedResource(resource, location, protocol, access);
    }

    private async Task RecordFailureAsync(
        JellyfinDbContext context,
        Guid itemId,
        StorageLocation location,
        NetworkStorageIssue issue,
        int? statusCode,
        NetworkStorageFailureSource source,
        CancellationToken cancellationToken)
    {
        await _failureWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            var failure = await context.NetworkStorageFailures.FirstOrDefaultAsync(f => f.ItemId.Equals(itemId), cancellationToken).ConfigureAwait(false);
            if (failure is null || !string.Equals(failure.Target, location.Target, StringComparison.Ordinal))
            {
                // A failure of another target the .strm named before says nothing of this one.
                if (failure is not null)
                {
                    context.NetworkStorageFailures.Remove(failure);
                }

                failure = new NetworkStorageFailure
                {
                    ItemId = itemId,
                    StorageKey = location.StorageKey,
                    Target = location.Target,
                    DateFirstFailed = now
                };
                context.NetworkStorageFailures.Add(failure);
            }

            failure.StorageKey = location.StorageKey;
            failure.Issue = (int)issue;
            failure.StatusCode = statusCode;
            failure.Source = (int)source;
            failure.DateLastFailed = now;
            failure.Count++;
            await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _failureWriteLock.Release();
        }
    }

    private async Task SaveCheckFailuresAsync(IReadOnlyCollection<NetworkStorageFailure> failures, CancellationToken cancellationToken)
    {
        await _failureWriteLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var context = await _dbProvider.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await using (context.ConfigureAwait(false))
            {
                // Playback or probing may have reported some of these while the check ran; the check's result replaces theirs.
                var ids = failures.Select(f => f.ItemId).ToArray();
                var reported = await context.NetworkStorageFailures.Where(f => ids.Contains(f.ItemId)).ToListAsync(cancellationToken).ConfigureAwait(false);
                context.NetworkStorageFailures.RemoveRange(reported);
                context.NetworkStorageFailures.AddRange(failures);
                await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _failureWriteLock.Release();
        }
    }

    private sealed record StoredAccess(NetworkStorageAccessDto Access, NetworkStorageExternalAddressDto External);

    private sealed record ResourceRow(Guid Id, string? Name, string? SeriesName, string Type, string Path, long? Size, string? Target);

    private sealed record DescribedResource(NetworkStorageResource Resource, StorageLocation? Location, INetworkStorageProtocol? ProtocolHandler, StoredAccess? Access)
    {
        public bool RequiresAccess => ProtocolHandler?.RequiresAccess ?? false;

        public string StorageKey => Location?.StorageKey ?? UnresolvedStorageKey;

        public string Container => Location?.Container ?? string.Empty;

        public string ProtocolName => Location?.Protocol ?? string.Empty;
    }
}
