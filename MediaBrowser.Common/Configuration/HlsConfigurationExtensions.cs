using MediaBrowser.Model.Configuration;

namespace MediaBrowser.Common.Configuration;

/// <summary>
/// Class containing extension methods for working with the settings of pre-encoded HLS.
/// </summary>
public static class HlsConfigurationExtensions
{
    /// <summary>
    /// Gets the settings of pre-encoded HLS.
    /// </summary>
    /// <param name="configurationManager">The configuration manager.</param>
    /// <returns>The settings.</returns>
    public static HlsOptions GetHlsOptions(this IConfigurationManager configurationManager)
        => configurationManager.GetConfiguration<HlsOptions>("hls");
}
