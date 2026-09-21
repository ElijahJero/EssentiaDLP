using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EssentiaDLP.Services;

public sealed class GalaxyException : Exception
{
    public GalaxyException(string message) : base(message) { }
}

public sealed class GalaxyClient
{
    private readonly IHttpClientFactory _http;

    public GalaxyClient(IHttpClientFactory http) => _http = http;

    public async Task<List<JsonElement>> PendingAsync(string url, string apiKey, int take, CancellationToken ct)
    {
        var payload = await RequestAsync(url, apiKey, HttpMethod.Get, $"/api/v1/tracks/pending-audio?take={take}", null, ct);
        if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            return items.EnumerateArray().Select(x => x.Clone()).ToList();
        return [];
    }

    public Task PutProfileAsync(string url, string apiKey, int trackId, JsonElement profile, CancellationToken ct) =>
        RequestAsync(url, apiKey, HttpMethod.Put, $"/api/v1/tracks/{trackId}/audio-profile", profile, ct);

    private async Task<JsonElement> RequestAsync(string url, string apiKey, HttpMethod method, string path, JsonElement? body, CancellationToken ct)
    {
        url = url.Trim().TrimEnd('/');
        if (string.IsNullOrWhiteSpace(url))
            throw new GalaxyException("Set a Galaxy Music URL (e.g. http://localhost:5107).");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new GalaxyException("Set a write API key from Galaxy Music Settings.");

        using var req = new HttpRequestMessage(method, url + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is { } payload)
            req.Content = new StringContent(payload.GetRawText(), Encoding.UTF8, "application/json");

        try
        {
            var client = _http.CreateClient(nameof(GalaxyClient));
            using var res = await client.SendAsync(req, ct);
            var raw = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new GalaxyException($"{method} {path} failed ({(int)res.StatusCode}): {raw}");
            if (string.IsNullOrWhiteSpace(raw))
                return default;
            using var doc = JsonDocument.Parse(raw);
            return doc.RootElement.Clone();
        }
        catch (HttpRequestException ex)
        {
            throw new GalaxyException($"Could not reach {url}: {ex.Message}");
        }
    }
}
