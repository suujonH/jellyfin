using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// A configuration that stores the settings of pre-encoded HLS.
/// </summary>
public class HlsConfigurationStore : ConfigurationStore, IValidatingConfiguration
{
    /// <summary>
    /// The name of the configuration in the storage.
    /// </summary>
    public const string StoreKey = "hls";

    /// <summary>
    /// Initializes a new instance of the <see cref="HlsConfigurationStore"/> class.
    /// </summary>
    public HlsConfigurationStore()
    {
        ConfigurationType = typeof(HlsOptions);
        Key = StoreKey;
    }

    /// <inheritdoc />
    public void Validate(object oldConfig, object newConfig)
    {
        if (HlsOptionsValidator.Validate((HlsOptions)newConfig) is { } error)
        {
            throw new ArgumentException(error, nameof(newConfig));
        }
    }
}
