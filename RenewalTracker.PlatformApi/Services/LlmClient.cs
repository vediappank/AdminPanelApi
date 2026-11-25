using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace RenewalTracker.PlatformApi.Services;

/// <summary>
/// Calls AI providers that speak the OpenAI "chat completions" format (Gemini, Groq, OpenRouter, Mistral, OpenAI...).
/// Providers are tried in the order of configuration section ChatAgent:Providers; when one fails (quota, key, outage) the next one answers.
/// A provider that returned 429 is skipped for 10 minutes. Keys live in appsettings.Production.json / environment variables, never in the website.
/// </summary>
public sealed class LlmClient
{
    public const string HttpClientName = "chat-agent";

    public sealed class Provider
    {
        public string Name { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;   // e.g. https://api.groq.com/openai/v1
        public string ApiKey { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
    }

    private readonly IHttpClientFactory _http;
    private readonly ILogger<LlmClient> _log;
    private readonly List<Provider> _providers;
    private readonly ConcurrentDictionary<string, DateTime> _coolDown = new();

    public LlmClient(IHttpClientFactory http, IConfiguration config, ILogger<LlmClient> log)
    {
        _http = http; _log = log;
        _providers = (config.GetSection("ChatAgent:Providers").Get<List<Provider>>() ?? new())
            .Where(p => !string.IsNullOrWhiteSpace(p.ApiKey) && !string.IsNullOrWhiteSpace(p.BaseUrl) && !string.IsNullOrWhiteSpace(p.Model)).ToList();
    }

    public bool Ready => _providers.Count > 0;

    public sealed record Result(JsonObject Message, string Provider);

    /// <summary>One model turn. Returns null when every provider failed.</summary>
    public async Task<Result?> CompleteAsync(JsonArray messages, JsonArray tools, CancellationToken ct)
    {
        foreach (var p in _providers)
        {
            if (_coolDown.TryGetValue(p.Name, out var until) && until > DateTime.UtcNow) continue;
            try
            {
                var body = new JsonObject
                {
                    ["model"] = p.Model,
                    ["messages"] = JsonNode.Parse(messages.ToJsonString()),
                    ["temperature"] = 0.3,
                };
                if (tools.Count > 0) { body["tools"] = JsonNode.Parse(tools.ToJsonString()); body["tool_choice"] = "auto"; }

                using var req = new HttpRequestMessage(HttpMethod.Post, p.BaseUrl.TrimEnd('/') + "/chat/completions")
                { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", p.ApiKey);
                if (p.BaseUrl.Contains("openrouter", StringComparison.OrdinalIgnoreCase))
                { req.Headers.Add("HTTP-Referer", "https://blisspointworld.com"); req.Headers.Add("X-Title", "Bliss Point website assistant"); }

                using var res = await _http.CreateClient(HttpClientName).SendAsync(req, ct);
                var text = await res.Content.ReadAsStringAsync(ct);
                if (!res.IsSuccessStatusCode)
                {
                    _log.LogWarning("AI provider {Provider} returned {Status}: {Body}", p.Name, (int)res.StatusCode, text.Length > 300 ? text[..300] : text);
                    if ((int)res.StatusCode == 429) _coolDown[p.Name] = DateTime.UtcNow.AddMinutes(10);
                    else if ((int)res.StatusCode is 401 or 403 or 404) _coolDown[p.Name] = DateTime.UtcNow.AddMinutes(30);
                    continue;
                }
                var msg = JsonNode.Parse(text)?["choices"]?[0]?["message"]?.AsObject();
                if (msg == null) { _log.LogWarning("AI provider {Provider} returned no message", p.Name); continue; }
                return new Result(msg, p.Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "AI provider {Provider} failed", p.Name);
                _coolDown[p.Name] = DateTime.UtcNow.AddMinutes(1);
            }
        }
        return null;
    }
}
