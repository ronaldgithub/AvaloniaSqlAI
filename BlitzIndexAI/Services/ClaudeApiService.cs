using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace BlitzIndexAI.Services;

public class ClaudeApiService
{
    private static readonly HttpClient Http = new();
    private const string ApiUrl = "https://api.anthropic.com/v1/messages";
    private const string Model = "claude-sonnet-4-6";
    private const string SplitMarker = "I need help analyzing";

    public async Task<string> SendPromptAsync(string fullPrompt, string apiKey)
    {
        string system, user;
        var splitIdx = fullPrompt.IndexOf(SplitMarker, StringComparison.Ordinal);
        if (splitIdx > 0)
        {
            system = fullPrompt[..splitIdx].Trim();
            user = fullPrompt[splitIdx..].Trim();
        }
        else
        {
            system = string.Empty;
            user = fullPrompt;
        }

        object payload = string.IsNullOrEmpty(system)
            ? new
            {
                model = Model,
                max_tokens = 4096,
                messages = new[] { new { role = "user", content = user } }
            }
            : new
            {
                model = Model,
                max_tokens = 4096,
                system,
                messages = new[] { new { role = "user", content = user } }
            };

        var json = JsonSerializer.Serialize(payload);
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiUrl);
        request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        request.Headers.Add("x-api-key", apiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await Http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Claude API returned {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;
    }
}
