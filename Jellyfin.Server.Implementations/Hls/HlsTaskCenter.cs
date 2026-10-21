using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.NetworkStorage;
using MediaBrowser.Model.Hls;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// The task center as the worker API and the management API use it: claims, identification and the end of jobs go to
/// the job store, and what they change is handed on to the published index, the coordinator and the verification.
/// </summary>
public sealed class HlsTaskCenter : IHlsTaskCenter
{
    private const int MaxWorkerName = 128;
    private const int MaxError = 2048;
    private const int MaxJobs = 1000;
    private const int MaxBase = 1024;

    private readonly HlsJobStore _store;
    private readonly HlsSettings _settings;
    private readonly HlsPublishedIndex _index;
    private readonly HlsCoordinator _coordinator;
    private readonly HlsVerificationService _verification;
    private readonly HlsRootMonitor _monitor;
    private readonly INetworkStorageManager _networkStorage;
    private readonly ILibraryManager _libraryManager;
    private readonly IServerApplicationPaths _appPaths;
    private readonly ILogger<HlsTaskCenter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsTaskCenter"/> class.
    /// </summary>
    /// <param name="store">The data of pre-encoded HLS.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="index">The published index.</param>
    /// <param name="coordinator">The coordinator.</param>
    /// <param name="verification">The verification.</param>
    /// <param name="monitor">The monitor of the roots of the HLS stores.</param>
    /// <param name="networkStorage">The network storage.</param>
    /// <param name="libraryManager">The library manager.</param>
    /// <param name="appPaths">The paths of the server.</param>
    /// <param name="logger">The logger.</param>
    public HlsTaskCenter(
        HlsJobStore store,
        HlsSettings settings,
        HlsPublishedIndex index,
        HlsCoordinator coordinator,
        HlsVerificationService verification,
        HlsRootMonitor monitor,
        INetworkStorageManager networkStorage,
        ILibraryManager libraryManager,
        IServerApplicationPaths appPaths,
        ILogger<HlsTaskCenter> logger)
    {
        _store = store;
        _settings = settings;
        _index = index;
        _coordinator = coordinator;
        _verification = verification;
        _monitor = monitor;
        _networkStorage = networkStorage;
        _libraryManager = libraryManager;
        _appPaths = appPaths;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<HlsClaimedJob?> PollAsync(string worker, CancellationToken cancellationToken)
    {
        ValidateWorker(worker);
        var settings = _settings.Current;
        if (!settings.Options.Enabled)
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
        var root = await GetUsableBaseAsync(identification.Base, cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        if (await _store.GetLeaseAsync(jobId, identification.LeaseToken, now, cancellationToken).ConfigureAwait(false) is not { } lease)
        {
            return null;
        }

        var previousSha256 = (await _store.GetSourceAsync(lease.Input.ItemId, cancellationToken).ConfigureAwait(false))?.Sha256;
        var settings = _settings.Current;
        var result = await _store.IdentifyAsync(
            new HlsIdentifyRequest(jobId, identification.LeaseToken, sha256, probe, root, settings.Options.Qualities, HlsEncodingProfile.Version, settings.IsLibraryEligible, now),
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
    public async Task<HlsStoreTestResult> CheckBaseAsync(string root, CancellationToken cancellationToken)
    {
        var (store, problem) = await CheckBaseCoreAsync(root, cancellationToken).ConfigureAwait(false);
        if (store is null || problem is not null)
        {
            return new HlsStoreTestResult(false, false, problem);
        }

        try
        {
            return new HlsStoreTestResult(true, await store.HasMarkerAsync(cancellationToken).ConfigureAwait(false), null);
        }
        catch (IOException ex)
        {
            return new HlsStoreTestResult(false, false, "The HLS store cannot be read: " + ex.Message);
        }
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
            settings.Lease,
            DateTime.UtcNow,
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<HlsStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        var counts = await _store.GetCountsAsync(cancellationToken).ConfigureAwait(false);

        // Only the roots still in use: a root whose renditions are all gone has nothing left to wait for.
        var problems = _monitor.GetProblems();
        var roots = await _store.GetRootsAsync(cancellationToken).ConfigureAwait(false);
        var storeProblems = roots
            .Where(problems.ContainsKey)
            .Select(root => new HlsStoreProblem(root, problems[root]))
            .ToList();
        return new HlsStatus(settings.Options.Enabled, settings.Error, storeProblems, counts, _verification.LastReverify);
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

    private static bool IsSameOrBelow(string path, string parent)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));

        // A root of the file system, such as / or D:\, keeps its separator.
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return path.Equals(parent, comparison) || path.StartsWith(prefix, comparison);
    }

    // The root a worker reports, in its normal form; one it may not upload to is refused with the reason.
    private async Task<string> GetUsableBaseAsync(string? root, CancellationToken cancellationToken)
    {
        var (store, problem) = await CheckBaseCoreAsync(root, cancellationToken).ConfigureAwait(false);
        return store is not null && problem is null ? store.Location : throw new ArgumentException(problem, nameof(root));
    }

    // The store of a root and why a worker may not upload to it, or null. A local root must be an existing directory that holds
    // no library and is not held by one, nor by the data of the server: deleting a rendition directory there could take a
    // library file or the database with it. Whether the root can be read and trusted is the check of the monitor.
    private async Task<(IHlsStore? Store, string? Problem)> CheckBaseCoreAsync(string? root, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || root.Length > MaxBase)
        {
            return (null, "The root of the HLS store is 1 to 1024 characters long.");
        }

        IHlsStore store;
        try
        {
            store = _monitor.Open(root);
        }
        catch (ArgumentException ex)
        {
            return (null, ex.Message);
        }

        if (store.IsLocal)
        {
            var path = store.Location;
            if (!Directory.Exists(path))
            {
                return (store, "The HLS store does not exist or cannot be read: " + path);
            }

            var libraries = _libraryManager.GetVirtualFolders().SelectMany(f => f.Locations);
            if (libraries.FirstOrDefault(library => IsSameOrBelow(path, library) || IsSameOrBelow(library, path)) is { } held)
            {
                return (store, "The HLS store must not overlap the library folder " + held + ".");
            }

            if (new[] { _appPaths.ProgramDataPath, _appPaths.DataPath }.FirstOrDefault(data => IsSameOrBelow(path, data)) is { } data)
            {
                return (store, "The HLS store must not be in the data of the server, " + data + ".");
            }
        }

        return (store, await _monitor.CheckAsync(store.Location, cancellationToken).ConfigureAwait(false));
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
