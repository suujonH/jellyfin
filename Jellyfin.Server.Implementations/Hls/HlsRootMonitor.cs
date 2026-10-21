using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Hls;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// Whoever decides anything from a file missing in an HLS store asks this first: verification, verifying again and deletion.
/// Every root is checked on its own, so nothing irreversible happens in a root that cannot be trusted while the others go on.
/// Only a change of the state of a root is logged.
/// </summary>
public sealed class HlsRootMonitor
{
    private const int SampleSize = 5;

    private readonly HlsJobStore _store;
    private readonly IHlsStoreFactory _storeFactory;
    private readonly HlsSettings _settings;
    private readonly ILogger<HlsRootMonitor> _logger;

    // The result of the last check of every root checked: null while it can be trusted, otherwise why not.
    private readonly ConcurrentDictionary<string, string?> _problems = new(StringComparer.Ordinal);

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsRootMonitor"/> class.
    /// </summary>
    /// <param name="store">The data of pre-encoded HLS.</param>
    /// <param name="storeFactory">The factory of HLS stores.</param>
    /// <param name="settings">The settings.</param>
    /// <param name="logger">The logger.</param>
    public HlsRootMonitor(HlsJobStore store, IHlsStoreFactory storeFactory, HlsSettings settings, ILogger<HlsRootMonitor> logger)
    {
        _store = store;
        _storeFactory = storeFactory;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>
    /// Gets the roots that could not be trusted when they were last checked, and why.
    /// </summary>
    /// <returns>The problems, by root.</returns>
    public IReadOnlyDictionary<string, string> GetProblems()
        => _problems.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!, StringComparer.Ordinal);

    /// <summary>
    /// Opens the store of a root. Whether it can be trusted is up to <see cref="CheckAsync"/>.
    /// </summary>
    /// <param name="root">The root: an absolute local path, or <c>s3://bucket/prefix</c>.</param>
    /// <returns>The store.</returns>
    /// <exception cref="ArgumentException">The root is neither.</exception>
    public IHlsStore Open(string root) => _storeFactory.Create(root);

    /// <summary>
    /// Checks a root, against the directories published last in it.
    /// </summary>
    /// <param name="root">The root, as it is recorded.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>null</c> when the root can be trusted, otherwise why not.</returns>
    public async Task<string?> CheckAsync(string root, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        string? problem;
        try
        {
            var store = Open(root);
            // Another spelling of a local root on Windows is the same directory, and must find what was published in it.
            var sample = await _store.GetPublishedSampleAsync(root, store.IsLocal && OperatingSystem.IsWindows(), SampleSize, cancellationToken).ConfigureAwait(false);
            problem = await store.CheckRootAsync(sample, cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException ex)
        {
            // A root recorded before roots were checked, or a sample that is not a path of the layout.
            problem = "The root of the HLS store is invalid: " + ex.Message;
        }

        if (_problems.TryGetValue(root, out var last) && string.Equals(problem, last, StringComparison.Ordinal))
        {
            return problem;
        }

        _problems[root] = problem;
        if (problem is null)
        {
            _logger.LogInformation("The HLS store can be used: {Root}", root);
        }
        else if (_settings.Current.Options.Enabled)
        {
            _logger.LogWarning("The HLS store {Root} cannot be used, so its verification and deletion wait: {Problem}", root, problem);
        }
        else
        {
            _logger.LogDebug("The HLS store {Root} cannot be used, so its verification and deletion wait: {Problem}", root, problem);
        }

        return problem;
    }
}
