using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Lararafelagid.Services
{
    public class PlausibleService
    {
        private readonly HttpClient _httpClient;
        private readonly string _siteId;
        private readonly string _apiKey;
        private readonly ILogger<PlausibleService> _logger;

        private static readonly JsonSerializerOptions _json = new()
        {
            PropertyNameCaseInsensitive = true
        };

        public PlausibleService(IHttpClientFactory clientFactory, IConfiguration config, ILogger<PlausibleService> logger)
        {
            _httpClient = clientFactory.CreateClient();
            _httpClient.Timeout = TimeSpan.FromSeconds(10);

            _siteId = config["Plausible:SiteId"] ?? throw new InvalidOperationException("Missing configuration: Plausible:SiteId");
            _apiKey = config["Plausible:ApiKey"] ?? throw new InvalidOperationException("Missing configuration: Plausible:ApiKey");
            _logger = logger;
        }

        public async Task<List<PlausibleResult>> GetMostViewedPages(int limit = 5, string period = "30d", string? hostname = null)
        {
            // 1) Primary: event:page + visitors
            var url1 = BuildUrl(property: "event:page", period: period, limit: limit, hostname: hostname);

            var res1 = await TryFetch(url1);
            if (res1.results.Count > 0)
                return res1.results;

            // 2) Fallback: page + visitors (some sites respond on legacy property name)
            var url2 = BuildUrl(property: "page", period: period, limit: limit, hostname: hostname);

            var res2 = await TryFetch(url2);
            return res2.results;
        }

        private string BuildUrl(string property, string period, int limit, string? hostname)
        {
            var url =
                $"https://plausible.io/api/v1/stats/breakdown" +
                $"?site_id={Uri.EscapeDataString(_siteId)}" +
                $"&period={Uri.EscapeDataString(period)}" +
                $"&property={Uri.EscapeDataString(property)}" +
                $"&metrics=visitors" +
                $"&limit={limit}";

            if (!string.IsNullOrWhiteSpace(hostname))
            {
                // strip any accidental port
                var hostOnly = hostname.Split(':')[0];
                // skip localhost/127.0.0.1 filter (no data there)
                var isLocal = hostOnly.Equals("localhost", StringComparison.OrdinalIgnoreCase) || hostOnly.Equals("127.0.0.1");
                if (!isLocal)
                {
                    var filters = $"event:hostname=={hostOnly}";
                    url += $"&filters={Uri.EscapeDataString(filters)}";
                }
            }

            return url;
        }

        private async Task<(List<PlausibleResult> results, bool ok)> TryFetch(string url)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                req.Headers.UserAgent.ParseAdd("Lararafelagid-Umbraco/1.0");

                using var res = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

                if (res.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                    res.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    var body = await res.Content.ReadAsStringAsync();
                    _logger.LogError("Plausible auth failed ({Status}). Url={Url}. Body={Body}", (int)res.StatusCode, url, body);
                    return (new List<PlausibleResult>(), false);
                }

                res.EnsureSuccessStatusCode();

                await using var stream = await res.Content.ReadAsStreamAsync();
                var payload = await JsonSerializer.DeserializeAsync<PlausibleApiResponse>(stream, _json);

                return (payload?.Results ?? new List<PlausibleResult>(), true);
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError(ex, "Plausible API timeout. Url={Url}", url);
                return (new List<PlausibleResult>(), false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Plausible API error. Url={Url}", url);
                return (new List<PlausibleResult>(), false);
            }
        }
    }

    public sealed class PlausibleApiResponse
    {
        [JsonPropertyName("results")]
        public List<PlausibleResult> Results { get; set; } = new();
    }

    public sealed class PlausibleResult
    {
        [JsonPropertyName("page")]
        public string Page { get; set; } = string.Empty;

        // v1 returns "visitors" when we ask metrics=visitors
        [JsonPropertyName("visitors")]
        public int Visitors { get; set; }
    }
}
