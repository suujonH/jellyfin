using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// Whoever decides anything from a file missing in the HLS store asks this first: verification, verifying again and deletion.
/// Nothing irreversible happens while the root of the store cannot be trusted. Only a change of the state is logged.
/// </summary>
public sealed class HlsRootMonitor
{
    private const int SampleSize = 5;

    private readonly HlsJobStore _store;
    private readonly ILogger<HlsRootMonitor> _logger;
    private readonly Lock _lock = new();
    private bool _checked;
    private string? _problem;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsRootMonitor"/> class.
    /// </summary>
    /// <param name="store">The data of pre-encoded HLS.</param>
    /// <param name="logger">The logger.</param>
    public HlsRootMonitor(HlsJobStore store, ILogger<HlsRootMonitor> logger)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// Gets the result of the last check: <c>null</c> while the root can be trusted, otherwise why not.
    /// </summary>
    public string? Problem => Volatile.Read(ref _problem);

    /// <summary>
    /// Checks the root of the HLS store the settings name.
    /// </summary>
    /// <param name="settings">The settings.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>null</c> when the root can be trusted, otherwise why not.</returns>
    public async Task<string?> CheckAsync(HlsSettingsSnapshot settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string? problem;
        if (settings.Store is { } store)
        {
            var sample = await _store.GetPublishedSampleAsync(SampleSize, cancellationToken).ConfigureAwait(false);
            problem = await store.CheckRootAsync(sample, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            problem = settings.Error is { } error ? "The settings of pre-encoded HLS are invalid: " + error : "No HLS store is set.";
        }

        lock (_lock)
        {
            if (_checked && string.Equals(problem, _problem, StringComparison.Ordinal))
            {
                return problem;
            }

            _checked = true;
            Volatile.Write(ref _problem, problem);
        }

        if (problem is null)
        {
            _logger.LogInformation("The HLS store can be used: {Store}", settings.Store!.Location);
        }
        else if (settings.Options.Enabled)
        {
            _logger.LogWarning("The HLS store cannot be used, so verification and deletion wait: {Problem}", problem);
        }
        else
        {
            _logger.LogDebug("The HLS store cannot be used, so verification and deletion wait: {Problem}", problem);
        }

        return problem;
    }
}
