using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Extensions.Json;
using MediaBrowser.Model.Configuration;

namespace Jellyfin.TrueCache.Tool;

/// <summary>
/// The settings of a running server that decide what an entry must hold, as its API reports them.
/// </summary>
/// <param name="System">The server configuration, with the trickplay and chapter settings.</param>
/// <param name="Encoding">The encoding configuration.</param>
internal sealed record ServerSettings(ServerConfiguration System, EncodingOptions Encoding)
{
    /// <summary>
    /// Reads the settings of a server.
    /// </summary>
    /// <param name="serverUrl">The address of the server.</param>
    /// <param name="apiKey">An API key of the server.</param>
    /// <returns>The settings.</returns>
    /// <exception cref="InvalidOperationException">The server cannot be reached or refuses the key.</exception>
    public static async Task<ServerSettings> FetchAsync(Uri serverUrl, string apiKey)
    {
        using var client = new HttpClient { BaseAddress = new Uri(serverUrl.AbsoluteUri.TrimEnd('/') + "/") };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "MediaBrowser Token=\"" + apiKey + "\"");
        try
        {
            var system = await client.GetFromJsonAsync<ServerConfiguration>("System/Configuration", JsonDefaults.Options).ConfigureAwait(false);
            var encoding = await client.GetFromJsonAsync<EncodingOptions>("System/Configuration/encoding", JsonDefaults.Options).ConfigureAwait(false);
            return system is not null && encoding is not null
                ? new ServerSettings(system, encoding)
                : throw new InvalidOperationException("The server at " + serverUrl + " reported no configuration.");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            throw new InvalidOperationException("The settings of the server at " + serverUrl + " cannot be read: " + ex.Message, ex);
        }
    }
}
