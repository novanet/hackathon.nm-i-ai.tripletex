using System.Net.Http.Json;
using System.Text.Json;

namespace TripletexAgent.Services;

/// <summary>
/// Searches Tripletex help articles via the public Zendesk Help Center API.
/// Used by FallbackAgentHandler to provide context about Tripletex operations.
/// </summary>
public class TripletexKnowledgeService
{
    private readonly HttpClient _http;
    private readonly ILogger<TripletexKnowledgeService> _logger;
    private const string BaseUrl = "https://hjelp.tripletex.no/api/v2/help_center/articles/search.json";

    public TripletexKnowledgeService(ILogger<TripletexKnowledgeService> logger)
    {
        _logger = logger;
        _http = new HttpClient();
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    /// <summary>
    /// Search Tripletex help articles. Returns a compact summary suitable for LLM context.
    /// </summary>
    public async Task<string> SearchAsync(string query, int maxResults = 3)
    {
        try
        {
            var url = $"{BaseUrl}?query={Uri.EscapeDataString(query)}&per_page={maxResults}";
            var response = await _http.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Zendesk search failed: {Status}", response.StatusCode);
                return "No help articles found.";
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var results = json.GetProperty("results");

            if (results.GetArrayLength() == 0)
                return "No help articles found.";

            var articles = new List<string>();
            foreach (var article in results.EnumerateArray())
            {
                var title = article.GetProperty("title").GetString() ?? "";
                var snippet = article.TryGetProperty("snippet", out var s) ? s.GetString() ?? "" : "";
                // Strip HTML tags from snippet
                snippet = System.Text.RegularExpressions.Regex.Replace(snippet, "<[^>]+>", "").Trim();
                if (snippet.Length > 500)
                    snippet = snippet[..500] + "...";
                articles.Add($"- **{title}**: {snippet}");
            }

            return string.Join("\n", articles);
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Zendesk search timed out for query: {Query}", query);
            return "Help article search timed out.";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Zendesk search error for query: {Query}", query);
            return "Help article search failed.";
        }
    }
}
