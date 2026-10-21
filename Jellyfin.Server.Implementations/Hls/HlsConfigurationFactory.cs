using System.Collections.Generic;
using MediaBrowser.Common.Configuration;

namespace Jellyfin.Server.Implementations.Hls;

/// <summary>
/// Factory for constructing the configuration of pre-encoded HLS.
/// </summary>
public class HlsConfigurationFactory : IConfigurationFactory
{
    /// <inheritdoc/>
    public IEnumerable<ConfigurationStore> GetConfigurations()
    {
        yield return new HlsConfigurationStore();
    }
}
