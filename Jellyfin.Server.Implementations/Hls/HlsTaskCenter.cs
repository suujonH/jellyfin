using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Model.Hls;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// The task center as the worker API and the management API use it: claims, identification, uploads and the end of jobs go to
/// the job store, and what they change is handed on to the published index, the coordinator and the verification.
/// </summary>
public sealed class HlsTaskCenter : IHlsTaskCenter
{
    private const int MaxWorkerName = 128;
    private const int MaxError = 2048;
    private const int MaxUploadFiles = 500;
    private const int MaxJobs = 1000;
    private const string UploadCheckFile = "check";

    private readonly HlsJobStore _store;
    private readonly HlsSettings _settings;
    private readonly HlsPublishedIndex _index;
    private readonly HlsCoordinator _coordinator;
    private readonly HlsVerificationService _verification;
    private readonly HlsRootMonitor _monitor;
    private readonly INetworkStorageManager _networkStorage;
    private readonly ILogger<HlsTaskCenter> _logger;

    // The upload checks of a local store that wait for their file, until their address expires.
    private readonly ConcurrentDictionary<Guid, DateTime> _uploadChecks = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsTaskCenter"/> class.
    /// </summary>
    /// <param name="store">The data of pre-encoded HLS.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="index">The published index.</param>
    /// <param name="coordinator">The coordinator.</param>
    /// <param name="verification">The verification.</param>
    /// <param name="monitor">The monitor of the root of the store.</param>
    /// <param name="networkStorage">The network storage.</param>
    /// <param name="logger">The logger.</param>
    public HlsTaskCenter(
        HlsJobStore store,
        HlsSettings settings,
        HlsPublishedIndex index,
        HlsCoordinator coordinator,
        HlsVerificationService verification,
        HlsRootMonitor monitor,
        INetworkStorageManager networkStorage,
        ILogger<HlsTaskCenter> logger)
    {
        _store = store;
        _settings = settings;
        _index = index;
        _coordinator = coordinator;
        _verification = verification;
        _monitor = monitor;
        _networkStorage = networkStorage;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HlsClaimedJob?> PollAsync(string worker, CancellationToken cancellationToken)
    {
        ValidateWorker(worker);
        var settings = _settings.Current;
        if (!settings.Options.Enabled || settings.Store is null)
        {
            return null;
        }

        var lease = await _store.ClaimAsync(new HlsClaimRequest(worker, settings.Lease, settings.IsLibraryEligible, DateTime.UtcNow), cancellationToken).ConfigureAwait(false);
        return lease is null
            ? null
            : new HlsClaimedJob(new HlsWorkLease(lease.Id, lease.LeaseToken, (int)settings.Lease.TotalSeconds, lease.Attempt), lease.Input);
    }

    /// <inheritdoc />
    public async Task<HlsJobInput?> GetInputAsync(Guid jobId, string leaseToken, CancellationToken cancellationToken)
    {
        if (await _store.GetLeaseAsync(jobId, leaseToken, DateTime.UtcNow, cancellationToken).ConfigureAwait(false) is not { } lease)
        {
            return null;
        }

        // A file that changed since the claim is sent as it is now: identifying it finds the change, and the job waits again.
        // The target of a .strm file is an address; a library file is a local path.
        var path = lease.Input.Path;
        if (!Uri.TryCreate(path, UriKind.Absolute, out var target) || target.IsFile)
        {
            return new HlsJobInput(path, null);
        }

        var address = _networkStorage.ResolveShortcutTarget(path);
        return Uri.TryCreate(address, UriKind.Absolute, out var uri)
            ? new HlsJobInput(null, uri)
            : throw new IOException("No access of network storage covers " + path + ".");
    }

    /// <inheritdoc />
    public Task<bool> HeartbeatAsync(Guid jobId, string leaseToken, CancellationToken cancellationToken)
        => _store.HeartbeatAsync(jobId, leaseToken, _settings.Current.Lease, DateTime.UtcNow, cancellationToken);

    /// <inheritdoc />
    public async Task<HlsIdentifyResponse?> IdentifyAsync(Guid jobId, HlsIdentification identification, CancellationToken cancellationToken)
    {
        var sha256 = HlsProbeReader.NormalizeSha256(identification.Sha256);
        var probe = ParseProbe(identification.ProbeJson);
        var now = DateTime.UtcNow;
        if (await _store.GetLeaseAsync(jobId, identification.LeaseToken, now, cancellationToken).ConfigureAwait(false) is not { } lease)
        {
            return null;
        }

        var previousSha256 = (await _store.GetSourceAsync(lease.Input.ItemId, cancellationToken).ConfigureAwait(false))?.Sha256;
        var settings = _settings.Current;
        var result = await _store.IdentifyAsync(
            new HlsIdentifyRequest(jobId, identification.LeaseToken, sha256, probe, settings.Options.Qualities, HlsEncodingProfile.Version, settings.IsLibraryEligible, now),
            cancellationToken).ConfigureAwait(false);
        if (result.Outcome == HlsIdentifyOutcome.LeaseLost)
        {
            return null;
        }

        if (result.KeptStoredProbe)
        {
            // Another ffprobe build may describe the same file differently; refusing it would leave the content without HLS.
            _logger.LogWarning("The probe of {Sha256} differs from the stored one, perhaps from another ffprobe; the stored one is kept", sha256);
        }

        // The file now has its hash, or lost it, and the renditions of both contents may have changed hands.
        // The change is committed, so a worker that hangs up now does not get to leave the index behind.
        string[] shas = previousSha256 is null || previousSha256 == sha256 ? [sha256] : [sha256, previousSha256];
        await FollowAsync(lease.Input.ItemId, shas).ConfigureAwait(false);
        return result.Outcome switch
        {
            HlsIdentifyOutcome.Done => new HlsIdentifyResponse(true, null),
            HlsIdentifyOutcome.Planned => new HlsIdentifyResponse(false, result.Plan),
            _ => null
        };
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HlsUploadTarget>?> GetUploadTargetsAsync(Guid jobId, HlsUploadRequest request, CancellationToken cancellationToken)
    {
        if (request.Files is not { Count: > 0 and <= MaxUploadFiles })
        {
            throw new ArgumentException("A request names 1 to 500 files.", nameof(request));
        }

        if (await GetRenditionAsync(jobId, request.LeaseToken, request.Key, cancellationToken).ConfigureAwait(false) is not { } found)
        {
            return null;
        }

        var (plan, rendition) = found;
        var store = await GetTrustedStoreAsync(_settings.Current, cancellationToken).ConfigureAwait(false);
        var targets = new List<HlsUploadTarget>(request.Files.Count);
        foreach (var file in request.Files)
        {
            // A null entry gets past the model validation.
            var name = file?.Path;
            var path = GetFilePath(plan, rendition, name);

            // A local store is written by the server, so the worker uploads to it.
            var address = store is LocalHlsStore
                ? null
                : store.GetUploadAddress(path, HlsSettingsSnapshot.UploadLifetime) ?? throw new IOException("No access of network storage covers the HLS store.");
            targets.Add(new HlsUploadTarget(name, address, GetContentType(rendition.Kind)));
        }

        return targets;
    }

    /// <inheritdoc />
    public async Task<bool> WriteFileAsync(Guid jobId, string leaseToken, string key, string file, Stream content, CancellationToken cancellationToken)
    {
        if (await GetRenditionAsync(jobId, leaseToken, key, cancellationToken).ConfigureAwait(false) is not { } found)
        {
            return false;
        }

        var path = GetFilePath(found.Plan, found.Rendition, file);
        var settings = _settings.Current;
        if (settings.Store is not LocalHlsStore store)
        {
            throw new ArgumentException("Workers upload straight to an HLS store in network storage.", nameof(file));
        }

        // Before every file: a mount lost during the uploads stops them.
        await GetTrustedStoreAsync(settings, cancellationToken).ConfigureAwait(false);
        await store.WriteAsync(path, content, cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> CompleteAsync(Guid jobId, HlsCompletion completion, CancellationToken cancellationToken)
    {
        if (!await _store.SubmitOutputsAsync(jobId, completion.LeaseToken, completion.Renditions, DateTime.UtcNow, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _verification.Signal();
        return true;
    }

    /// <inheritdoc />
    public Task<bool> FailAsync(Guid jobId, HlsFailure failure, CancellationToken cancellationToken)
    {
        var error = failure.Error ?? string.Empty;
        var settings = _settings.Current;
        return _store.ReleaseAsync(
            jobId,
            failure.LeaseToken,
            error.Length > MaxError ? error[..MaxError] : error,
            settings.Options.MaxAttempts,
            settings.UploadGrace,
            DateTime.UtcNow,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<HlsUploadCheck> StartUploadCheckAsync(string worker, CancellationToken cancellationToken)
    {
        ValidateWorker(worker);
        var settings = _settings.Current;
        var store = await GetTrustedStoreAsync(settings, cancellationToken).ConfigureAwait(false);
        var id = Guid.NewGuid();
        var directory = HlsStorePaths.GetUploadCheckDirectory(id);
        var path = directory + "/" + UploadCheckFile;
        var now = DateTime.UtcNow;
        Uri? address = null;
        if (store is LocalHlsStore)
        {
            foreach (var expired in _uploadChecks.Where(c => c.Value <= now).Select(c => c.Key))
            {
                _uploadChecks.TryRemove(expired, out _);
            }
        }
        else
        {
            address = store.GetUploadAddress(path, HlsSettingsSnapshot.UploadLifetime) ?? throw new IOException("No access of network storage covers the HLS store.");
        }

        // Whatever the worker uploads is deleted once the address expired, even after a restart.
        await _store.QueueUploadCheckDeletionAsync(directory, now + settings.UploadGrace, cancellationToken).ConfigureAwait(false);
        if (address is null)
        {
            _uploadChecks[id] = now + HlsSettingsSnapshot.UploadLifetime;
        }

        _logger.LogInformation("HLS worker {Worker} checks its uploads to the HLS store", worker);
        return new HlsUploadCheck(id, path, address);
    }

    /// <inheritdoc />
    public async Task<bool> WriteUploadCheckAsync(Guid checkId, Stream content, CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        if (!_uploadChecks.TryGetValue(checkId, out var expires) || expires <= DateTime.UtcNow || settings.Store is not LocalHlsStore store)
        {
            return false;
        }

        // A check whose upload failed can be tried again until it expires.
        await GetTrustedStoreAsync(settings, cancellationToken).ConfigureAwait(false);
        var directory = HlsStorePaths.GetUploadCheckDirectory(checkId);
        await store.WriteAsync(directory + "/" + UploadCheckFile, content, cancellationToken).ConfigureAwait(false);
        await store.DeleteDirectoryAsync(directory, cancellationToken).ConfigureAwait(false);
        return _uploadChecks.TryRemove(checkId, out _);
    }

    /// <inheritdoc />
    public async Task<HlsStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        var counts = await _store.GetCountsAsync(cancellationToken).ConfigureAwait(false);
        return new HlsStatus(settings.Options.Enabled, settings.Error, _monitor.Problem, counts, _verification.LastReverify);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<HlsJobSummary>> GetJobsAsync(int limit, CancellationToken cancellationToken)
        => _store.ListJobsAsync(Math.Clamp(limit, 1, MaxJobs), cancellationToken);

    /// <inheritdoc />
    public void RequestRebuild() => _coordinator.RequestRebuild();

    /// <inheritdoc />
    public bool StartReverify(Guid? itemId) => _verification.StartReverify(itemId);

    private static void ValidateWorker(string worker)
    {
        if (string.IsNullOrWhiteSpace(worker) || worker.Length > MaxWorkerName)
        {
            throw new ArgumentException("The name of a worker is 1 to 128 characters long.", nameof(worker));
        }
    }

    private static HlsProbe ParseProbe(string json)
    {
        try
        {
            return HlsProbeReader.Parse(json);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new ArgumentException("The probe is not the JSON output of ffprobe: " + ex.Message, nameof(json), ex);
        }
    }

    // The path in the store of a file of the plan; a name the rendition cannot have is refused, and so is a missing one.
    private static string GetFilePath(HlsEncodePlan plan, HlsPlannedRendition rendition, [NotNull] string? file)
        => file is not null && HlsRenditionPaths.IsAllowedFile(rendition.Kind, file, plan.ExpectedSegments)
            ? rendition.RelativeDirectory + "/" + file
            : throw new ArgumentException("Rendition " + rendition.Key + " cannot have a file named " + file + ".", nameof(file));

    private static string GetContentType(HlsRenditionKind kind) => kind switch
    {
        HlsRenditionKind.Video => "video/mp4",
        HlsRenditionKind.Audio => "audio/mp4",
        HlsRenditionKind.Subtitle => "text/vtt",
        HlsRenditionKind.Preview => "image/jpeg",
        _ => throw new ArgumentException("Rendition kind " + kind + " has no files.", nameof(kind))
    };

    // Nothing is uploaded into a root that cannot be trusted: a local one that lost its mount would fill the disk beneath it.
    private async Task<IHlsStore> GetTrustedStoreAsync(HlsSettingsSnapshot settings, CancellationToken cancellationToken)
    {
        var store = settings.Store ?? throw new IOException("No HLS store is set.");
        return await _monitor.CheckAsync(settings, cancellationToken).ConfigureAwait(false) is { } problem ? throw new IOException(problem) : store;
    }

    // The plan of a job a worker holds the lease of, and one rendition of it; null when the lease is not valid.
    private async Task<(HlsEncodePlan Plan, HlsPlannedRendition Rendition)?> GetRenditionAsync(Guid jobId, string leaseToken, string key, CancellationToken cancellationToken)
    {
        if (await _store.GetLeaseAsync(jobId, leaseToken, DateTime.UtcNow, cancellationToken).ConfigureAwait(false) is not { } lease)
        {
            return null;
        }

        var plan = lease.Plan ?? throw new ArgumentException("The job has no plan until its file is identified.", nameof(jobId));
        var rendition = plan.Renditions.FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.Ordinal))
            ?? throw new ArgumentException("The plan has no rendition " + key + ".", nameof(key));
        return (plan, rendition);
    }

    // After a commit: playback and scheduling follow. The two refreshes are separate, as a refresh that fails is repeated by the index,
    // but only for what it was asked to read.
    private async Task FollowAsync(Guid itemId, IReadOnlyList<string> sha256s)
    {
        foreach (var sha256 in sha256s)
        {
            _coordinator.EnqueueSchedule(sha256);
        }

        try
        {
            await _index.RefreshItemsAsync([itemId], CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refreshing the HLS index of item {ItemId} failed, it is tried again", itemId);
        }

        try
        {
            await _index.RefreshMediaAsync(sha256s, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Refreshing the published HLS renditions of {Sha256s} failed, it is tried again", sha256s);
        }
    }
}
