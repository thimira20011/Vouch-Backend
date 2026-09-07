using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Vouch.Application.Common.Interfaces;
using Vouch.Application.Features.AiWingman;
using Vouch.Domain.Entities;

namespace Vouch.Infrastructure.Services;

public class AnthropicAiWingmanService : IAiWingmanService
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<AnthropicAiWingmanService> _logger;

    public AnthropicAiWingmanService(
        HttpClient httpClient,
        IConfiguration configuration,
        ILogger<AnthropicAiWingmanService> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<IReadOnlyList<IcebreakerSuggestion>> GenerateIcebreakersAsync(
        User userA,
        User userB,
        CancellationToken cancellationToken = default)
    {
        // 1. Identify shared intellectual interests and values
        var sharedInterests = userA.IntellectualInterests.Intersect(userB.IntellectualInterests).ToList();
        var sharedValues = userA.DeepValues.Intersect(userB.DeepValues, StringComparer.OrdinalIgnoreCase).ToList();

        var apiKey = _configuration["Anthropic:ApiKey"];

        // If no API key configured, immediately use curated fallback library (REQ-26)
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogInformation("No Anthropic API key configured; using curated static fallback library.");
            return GetCuratedFallbackSuggestions(sharedInterests);
        }

        // 2. Prepare LLM Call with strict 3-second timeout budget (REQ-27)
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(3));

        try
        {
            var prompt = $"""
You are the AI Wingman for Vouch, a university connection platform designed around 'Monastic Minimalism' and intentional, slow conversation.
User A values: {string.Join(", ", userA.DeepValues)}, interests: {string.Join(", ", userA.IntellectualInterests)}.
User B values: {string.Join(", ", userB.DeepValues)}, interests: {string.Join(", ", userB.IntellectualInterests)}.
Shared themes: {string.Join(", ", sharedValues)} and {string.Join(", ", sharedInterests)}.

Generate exactly 3 unique, thoughtful, considered icebreaker questions that could open a slow, letter-style conversation.
Return strictly a JSON array of strings containing the 3 questions, nothing else.
Example: ["question 1", "question 2", "question 3"]
""";

            var requestPayload = new
            {
                model = "claude-3-5-haiku-latest",
                max_tokens = 300,
                messages = new[]
                {
                    new { role = "user", content = prompt }
                }
            };

            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            requestMessage.Headers.Add("x-api-key", apiKey);
            requestMessage.Headers.Add("anthropic-version", "2023-06-01");
            requestMessage.Content = JsonContent.Create(requestPayload);

            var response = await _httpClient.SendAsync(requestMessage, cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Anthropic API returned status {StatusCode}. Falling back.", response.StatusCode);
                return GetCuratedFallbackSuggestions(sharedInterests);
            }

            var jsonResult = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
            var contentText = jsonResult.GetProperty("content")[0].GetProperty("text").GetString();

            if (!string.IsNullOrEmpty(contentText))
            {
                var questions = JsonSerializer.Deserialize<List<string>>(contentText);
                if (questions != null && questions.Count > 0)
                {
                    return questions.Take(3).Select(q => new IcebreakerSuggestion(
                        Text: q,
                        GroundingTheme: sharedInterests.FirstOrDefault().ToString() ?? "Intellectual Curiosity",
                        IsFromAi: true
                    )).ToList();
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Anthropic API exceeded 3-second latency budget (REQ-27). Reverting to curated static fallback.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate AI icebreakers. Reverting to curated static fallback.");
        }

        // Automatic fallback (REQ-26, REQ-27)
        return GetCuratedFallbackSuggestions(sharedInterests);
    }

    private static IReadOnlyList<IcebreakerSuggestion> GetCuratedFallbackSuggestions(
        IEnumerable<Domain.Enums.IntellectualInterest> sharedInterests)
    {
        var fallbacks = CuratedIcebreakers.GetIcebreakersForInterests(sharedInterests, count: 3);
        return fallbacks.Select(f => new IcebreakerSuggestion(
            Text: f.Text,
            GroundingTheme: f.Interest.ToString(),
            IsFromAi: false
        )).ToList();
    }
}
