using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Jellyfin.MediaEncoding.Hls.Published;
using MediaBrowser.Controller.Hls;
using MediaBrowser.Model.Hls;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// Publishes the outputs a worker submitted once every reported file is in its HLS store, and verifies published renditions
/// again on request. Nothing fails in a root that cannot be trusted: the files may only seem missing.
/// </summary>
public sealed class HlsVerificationService : BackgroundService
{
    private const int BatchSize = 4;

    private static readonly TimeSpan _interval = TimeSpan.FromSeconds(10);

    private readonly HlsJobStore _store;
    private readonly HlsSettings _settings;
    private readonly HlsRootMonitor _monitor;
    private readonly HlsPublishedIndex _index;
    private readonly HlsCoordinator _coordinator;
    private readonly ILogger<HlsVerificationService> _logger;
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stopping = new();
    private int _reverifying;
    private HlsReverifyReport? _lastReverify;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsVerificationService"/> class.
    /// </summary>
    /// <param name="store">The data of pre-encoded HLS.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="monitor">The monitor of the roots of the HLS stores.</param>
    /// <param name="index">The published index.</param>
    /// <param name="coordinator">The coordinator.</param>
    /// <param name="logger">The logger.</param>
    public HlsVerificationService(
        HlsJobStore store,
        HlsSettings settings,
        HlsRootMonitor monitor,
        HlsPublishedIndex index,
        HlsCoordinator coordinator,
        ILogger<HlsVerificationService> logger)
    {
        _store = store;
        _settings = settings;
        _monitor = monitor;
        _index = index;
        _coordinator = coordinator;
        _logger = logger;
    }

    /// <summary>
    /// Gets the result of the last verification of published renditions, or <c>null</c> when none ran.
    /// </summary>
    public HlsReverifyReport? LastReverify => Volatile.Read(ref _lastReverify);

    /// <summary>
    /// Verifies the submitted outputs now, rather than in up to ten seconds.
    /// </summary>
    public void Signal() => _signal.Writer.TryWrite(true);

    /// <summary>
    /// Starts verifying published renditions again, in the background. A rendition that fails is retired, so it is encoded again.
    /// </summary>
    /// <param name="itemId">The item whose content is verified, or <c>null</c> for every content.</param>
    /// <returns>Whether it started; <c>false</c> while it is running already.</returns>
    public bool StartReverify(Guid? itemId)
    {
        if (Interlocked.CompareExchange(ref _reverifying, 1, 0) != 0)
        {
            return false;
        }

        var cancellationToken = _stopping.Token;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await ReverifyAsync(itemId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(ex, "Verifying the published HLS renditions again failed");
                }
                finally
                {
                    Volatile.Write(ref _reverifying, 0);
                }
            },
            CancellationToken.None);
        return true;
    }

    /// <inheritdoc />
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override void Dispose()
    {
        _stopping.Dispose();
        base.Dispose();
    }

    /// <summary>
    /// Verifies the outputs of the jobs submitted longest ago, each in the root its worker uploaded to; a root that cannot be
    /// trusted leaves its jobs verifying, and the batch is read again without it, so the jobs of the other roots are not held up.
    /// A job whose verification throws stays verifying and is tried again in the next round.
    /// </summary>
    /// <param name="now">The current time.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    internal async Task VerifyBatchAsync(DateTime now, CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        bool skippedMore;
        do
        {
            IReadOnlyList<HlsVerifyingJob> jobs;
            try
            {
                jobs = await _store.GetVerifyingJobsAsync(BatchSize, skipped, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Reading the HLS jobs to verify failed");
                return;
            }

            // Every round skips one root more or is the last, so this ends.
            skippedMore = false;
            foreach (var root in jobs.GroupBy(j => j.Base, StringComparer.Ordinal))
            {
                if (!await IsTrustedAsync(root.Key, cancellationToken).ConfigureAwait(false))
                {
                    skippedMore |= skipped.Add(root.Key);
                    continue;
                }

                await VerifyRootAsync(root.Key, root, settings, now, cancellationToken).ConfigureAwait(false);
            }
        }
        while (skippedMore);
    }

    private async Task<bool> IsTrustedAsync(string root, CancellationToken cancellationToken)
    {
        try
        {
            return await _monitor.CheckAsync(root, cancellationToken).ConfigureAwait(false) is null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "Checking the HLS store {Root} failed", root);
            return false;
        }
    }

    private async Task VerifyRootAsync(string root, IEnumerable<HlsVerifyingJob> jobs, HlsSettingsSnapshot settings, DateTime now, CancellationToken cancellationToken)
    {
        var store = _monitor.Open(root);
        foreach (var job in jobs)
        {
            try
            {
                await VerifyAsync(job, settings, store, now, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogError(ex, "Verifying HLS job {JobId} failed, it is tried again", job.Id);
            }
        }
    }

    /// <summary>
    /// Verifies the published renditions of one content, or of every content one at a time, since the manifests of a whole
    /// library may not fit in memory. A rendition that fails is retired, but never while its root cannot be trusted: the rest
    /// of that root is then skipped, the other roots go on, and the report says why.
    /// </summary>
    /// <param name="itemId">The item whose content is verified, or <c>null</c> for every content.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task.</returns>
    internal async Task ReverifyAsync(Guid? itemId, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> shas;
        if (itemId is { } id)
        {
            if (_index.GetSource(id)?.Sha256 is not { } sha)
            {
                Report(0, 0, null);
                return;
            }

            shas = [sha];
        }
        else
        {
            shas = await _store.GetPublishedShasAsync(cancellationToken).ConfigureAwait(false);
        }

        int checkedCount = 0, failed = 0;

        // Every root is checked once before its first rendition, and again after every failure; one that cannot be trusted
        // is skipped from then on.
        var roots = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var sha in shas)
        {
            var retired = false;
            try
            {
                foreach (var manifest in await _store.GetPublishedManifestsAsync(sha, cancellationToken).ConfigureAwait(false))
                {
                    if (!roots.TryGetValue(manifest.Base, out var problem))
                    {
                        problem = await _monitor.CheckAsync(manifest.Base, cancellationToken).ConfigureAwait(false);
                        roots[manifest.Base] = problem;
                    }

                    if (problem is not null)
                    {
                        continue;
                    }

                    checkedCount++;
                    if (await VerifyOutputAsync(_monitor.Open(manifest.Base), manifest.RelativeDirectory, manifest.Files, cancellationToken).ConfigureAwait(false) is not { } error)
                    {
                        continue;
                    }

                    // Every failure asks whether the root is still there; if it is not, nothing more is retired from it.
                    problem = await _monitor.CheckAsync(manifest.Base, cancellationToken).ConfigureAwait(false);
                    roots[manifest.Base] = problem;
                    if (problem is not null)
                    {
                        continue;
                    }

                    failed++;
                    _logger.LogWarning("The published HLS rendition {Sha256}/{Key} failed verification: {Error}", manifest.Sha256, manifest.Key, error);
                    retired |= await _store.RetireRenditionAsync(manifest.Sha256, manifest.Key, manifest.RelativeDirectory, "Verification failed: " + error, DateTime.UtcNow, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // One content that cannot be read or retired does not end the run.
                _logger.LogError(ex, "Verifying the published HLS renditions of {Sha256} failed, going on with the next content", sha);
            }

            // For each content, so playback stops using a retired directory before the deletion queue reaches it.
            if (retired)
            {
                await FollowAsync(sha, cancellationToken).ConfigureAwait(false);
            }
        }

        var skipped = roots.Where(r => r.Value is not null).Select(r => r.Key + ": " + r.Value).ToList();
        Report(checkedCount, failed, skipped.Count == 0 ? null : string.Join("; ", skipped));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A signal sent before the start must not make the host wait for a verification.
        await Task.Yield();
        try
        {
            while (true)
            {
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken))
                {
                    wait.CancelAfter(_interval);
                    try
                    {
                        await _signal.Reader.ReadAsync(wait.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                    {
                        // Ten seconds without a signal.
                    }
                }

                await VerifyBatchAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // An exception leaving a BackgroundService stops the whole server.
            _logger.LogCritical(ex, "The HLS verification stopped unexpectedly");
        }
    }

    // A store that cannot be read fails the verification too, once the root is found trustworthy.
    private static async Task<string?> VerifyOutputAsync(IHlsStore store, string relativeDirectory, IReadOnlyList<HlsOutputFile> files, CancellationToken cancellationToken)
    {
        // The files are on the worker, which the server cannot read: the list the worker reported is taken as it is.
        if (HlsWorkerLocalAddress.TryParse(store.Location, out _, out _))
        {
            return null;
        }

        try
        {
            return HlsOutputVerifier.Compare(files, await store.ListAsync(relativeDirectory, cancellationToken).ConfigureAwait(false));
        }
        catch (IOException ex)
        {
            return "The HLS store cannot be read: " + ex.Message;
        }
    }

    private async Task VerifyAsync(HlsVerifyingJob job, HlsSettingsSnapshot settings, IHlsStore store, DateTime now, CancellationToken cancellationToken)
    {
        string? error = null;
        foreach (var output in job.Outputs)
        {
            if (await VerifyOutputAsync(store, output.RelativeDirectory, output.Files, cancellationToken).ConfigureAwait(false) is { } problem)
            {
                error = output.Key + ": " + problem;
                break;
            }
        }

        if (error is null)
        {
            if (await _store.PublishAsync(job.Id, root => GetReplacedGrace(root, settings), now, cancellationToken).ConfigureAwait(false) is { } result)
            {
                await FollowAsync(result.Sha256, cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        // The files may only be missing because the root became unavailable since the batch began; the job then stays verifying.
        if (await _monitor.CheckAsync(job.Base, cancellationToken).ConfigureAwait(false) is not null)
        {
            return;
        }

        if (await _store.FailVerificationAsync(job.Id, "Verification failed: " + error, settings.Options.MaxAttempts, now, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogWarning("The outputs of HLS job {JobId} ({Sha256}) failed verification: {Error}", job.Id, job.Sha256, error);
            _coordinator.EnqueueSchedule(job.Sha256);
        }
    }

    // A replaced directory stays as long as a playback grant issued for it may be used, and as long as the direct addresses of
    // its files that media playlists gave out are valid.
    private TimeSpan GetReplacedGrace(string? root, HlsSettingsSnapshot settings)
    {
        var grant = TimeSpan.FromHours(settings.Options.PlaybackTokenHours);
        if (string.IsNullOrEmpty(root))
        {
            return grant;
        }

        try
        {
            var addresses = _monitor.Open(root).GetClientAddressLifetime();
            return addresses > grant ? addresses : grant;
        }
        catch (ArgumentException)
        {
            return grant;
        }
    }

    // After a commit that changed what is published: a refresh that fails is repeated by the index itself, so it does not
    // turn the commit into a failure.
    private async Task FollowAsync(string sha256, CancellationToken cancellationToken)
    {
        _coordinator.EnqueueSchedule(sha256);
        try
        {
            await _index.RefreshMediaAsync([sha256], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Refreshing the published HLS renditions of {Sha256} failed, it is tried again", sha256);
        }
    }

    private void Report(int checkedCount, int failed, string? aborted)
        => Volatile.Write(ref _lastReverify, new HlsReverifyReport(DateTime.UtcNow, checkedCount, failed, aborted));
}
