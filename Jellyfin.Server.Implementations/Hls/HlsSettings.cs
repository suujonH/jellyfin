using System;
using System.Threading;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// Follows the settings of pre-encoded HLS: every save is checked again, so the services read one consistent snapshot.
/// </summary>
public sealed class HlsSettings : IDisposable
{
    private readonly IConfigurationManager _configurationManager;
    private readonly ILogger<HlsSettings> _logger;
    private readonly Lock _lock = new();
    private HlsSettingsSnapshot _current;

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsSettings"/> class.
    /// </summary>
    /// <param name="configurationManager">The configuration manager.</param>
    /// <param name="logger">The logger.</param>
    public HlsSettings(IConfigurationManager configurationManager, ILogger<HlsSettings> logger)
    {
        _configurationManager = configurationManager;
        _logger = logger;
        _current = Apply(configurationManager.GetHlsOptions(), 0);
        configurationManager.NamedConfigurationUpdated += OnNamedConfigurationUpdated;
    }

    /// <summary>
    /// Gets the settings as they are applied now.
    /// </summary>
    public HlsSettingsSnapshot Current => Volatile.Read(ref _current);

    /// <inheritdoc />
    public void Dispose() => _configurationManager.NamedConfigurationUpdated -= OnNamedConfigurationUpdated;

    private void OnNamedConfigurationUpdated(object? sender, ConfigurationUpdateEventArgs e)
    {
        if (!string.Equals(e.Key, HlsConfigurationStore.StoreKey, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        lock (_lock)
        {
            Volatile.Write(ref _current, Apply((HlsOptions)e.NewConfiguration, _current.Version + 1));
        }
    }

    // Saved settings are checked when they are saved; a file edited by hand may still be invalid, which leaves pre-encoded HLS off.
    private HlsSettingsSnapshot Apply(HlsOptions options, int version)
    {
        if (HlsOptionsValidator.Validate(options) is { } error)
        {
            _logger.LogError("The settings of pre-encoded HLS are invalid, so it is off: {Error}", error);
            return new HlsSettingsSnapshot(new HlsOptions(), error, version);
        }

        return new HlsSettingsSnapshot(options, null, version);
    }
}
