using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Regata.Desktop;

internal sealed class GroqTutorClient
{
    private static readonly Uri Endpoint = new("https://api.groq.com/openai/v1/chat/completions");
    private const string Model = "llama-3.3-70b-versatile";
    private const int MaximumQuestionLength = 4000;
    private const int MaximumCodeLength = 12000;

    public async Task<string> GetGuidanceAsync(
        HttpClient httpClient,
        string apiKey,
        string question,
        string? codeContext,
        CancellationToken cancellationToken = default)
    {
        if (question.Length > MaximumQuestionLength)
        {
            throw new InvalidOperationException($"Keep your question under {MaximumQuestionLength} characters.");
        }

        var prompt = new StringBuilder(question);
        if (!string.IsNullOrWhiteSpace(codeContext))
        {
            var clippedCode = codeContext.Length > MaximumCodeLength
                ? codeContext[..MaximumCodeLength] + Environment.NewLine + "[File excerpt shortened]"
                : codeContext;
            prompt.AppendLine();
            prompt.AppendLine();
            prompt.AppendLine("The learner chose to include this current-file excerpt as context. Do not edit the file:");
            prompt.AppendLine("```");
            prompt.AppendLine(clippedCode);
            prompt.AppendLine("```");
        }

        var requestBody = new
        {
            model = Model,
            temperature = 0.3,
            max_tokens = 900,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You are Regata, a patient programming tutor for beginner web developers learning HTML, CSS, and JavaScript. Teach instead of taking over. Start with one short hint or a question that helps the learner think. Explain the relevant concept simply. Give a small code example only when useful, explain each new piece, and invite the learner to type or try it themselves. Do not claim to edit files or build the whole project. Be kind, concise, and specific."
                },
                new { role = "user", content = prompt.ToString() }
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Groq returned HTTP {(int)response.StatusCode}. Check your key, internet connection, and account limits.",
                null,
                response.StatusCode);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("choices", out var choices)
            || choices.GetArrayLength() == 0
            || !choices[0].TryGetProperty("message", out var message)
            || !message.TryGetProperty("content", out var content)
            || content.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(content.GetString()))
        {
            throw new InvalidOperationException("Groq returned a response Regata could not read.");
        }

        return content.GetString()!;
    }
}
