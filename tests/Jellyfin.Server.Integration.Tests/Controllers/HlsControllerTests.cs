using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Hls;
using Xunit;

namespace Jellyfin.Server.Integration.Tests.Controllers
{
    public sealed class HlsControllerTests : IClassFixture<JellyfinApplicationFactory>
    {
        private readonly JellyfinApplicationFactory _factory;
        private static string? _accessToken;

        public HlsControllerTests(JellyfinApplicationFactory factory)
        {
            _factory = factory;
        }

        [Fact]
        public async Task Status_AndSettings_AreForAdministrators()
        {
            var client = await CreateAdministratorClientAsync();

            var status = await client.GetFromJsonAsync<HlsStatus>("Hls/Status", TestContext.Current.CancellationToken);
            var settings = await client.GetFromJsonAsync<HlsOptions>("Hls/Settings", TestContext.Current.CancellationToken);

            Assert.False(status!.Enabled);
            Assert.False(settings!.Enabled);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("Hls/Status", TestContext.Current.CancellationToken)).StatusCode);
        }

        [Fact]
        public async Task SaveSettings_RefusesInvalidSettings_WithTheReason()
        {
            var client = await CreateAdministratorClientAsync();

            var response = await client.PutAsJsonAsync("Hls/Settings", new HlsOptions { Enabled = true }, TestContext.Current.CancellationToken);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("store", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), System.StringComparison.Ordinal);
        }

        [Fact]
        public async Task TheWorkerApi_IsForApiKeys_SpeakingTheProtocol()
        {
            var client = await CreateAdministratorClientAsync();
            var created = await client.PostAsync("Auth/Keys?app=hls-worker", null, TestContext.Current.CancellationToken);
            created.EnsureSuccessStatusCode();
            var keys = await client.GetFromJsonAsync<JsonDocument>("Auth/Keys", TestContext.Current.CancellationToken);
            var apiKey = keys!.RootElement.GetProperty("Items")[0].GetProperty("AccessToken").GetString()!;
            var worker = _factory.CreateClient();
            worker.DefaultRequestHeaders.AddAuthHeader(apiKey);

            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("HlsWorker/Protocol", TestContext.Current.CancellationToken)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await worker.GetAsync("HlsWorker/Protocol", TestContext.Current.CancellationToken)).StatusCode);

            worker.DefaultRequestHeaders.Add(HlsWorkerProtocol.Header, "1");
            Assert.Equal(new HlsProtocolVersion(1), await worker.GetFromJsonAsync<HlsProtocolVersion>("HlsWorker/Protocol", TestContext.Current.CancellationToken));

            // Pre-encoded HLS is off, so there is no work.
            var poll = await worker.PostAsJsonAsync("HlsWorker/Poll", new HlsWorkerRequest("worker"), TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NoContent, poll.StatusCode);
        }

        private async Task<HttpClient> CreateAdministratorClientAsync()
        {
            var client = _factory.CreateClient();
            client.DefaultRequestHeaders.AddAuthHeader(_accessToken ??= await AuthHelper.CompleteStartupAsync(client));
            return client;
        }
    }
}
