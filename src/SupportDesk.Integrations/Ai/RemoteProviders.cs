using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SupportDesk.Application.Ai;

namespace SupportDesk.Integrations.Ai;

/// <summary>Typed HttpClient for OpenAI Chat Completions / Embeddings (OpenAI, Azure OpenAI deployments, Ollama/vLLM/LM Studio and other compatible servers).</summary>
public sealed class OpenAiCompatibleClient(HttpClient http)
{
    public const string DefaultOpenAiEndpoint = "https://api.openai.com/v1";
    public const string AzureApiVersion = "2024-10-21";

    public async Task<JsonNode> PostAsync(AiProviderSettings s, string operation, string model, JsonObject body, CancellationToken ct)
    {
        var (url, azure) = Url(s, operation, model);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        if (!string.IsNullOrEmpty(s.ApiKey))
        {
            if (azure) request.Headers.Add("api-key", s.ApiKey);
            else request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ApiKey);
        }
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AiProviderException($"{s.Provider} returned {(int)response.StatusCode} {response.ReasonPhrase}: {ErrorMessage(text)}", (int)response.StatusCode);
        return JsonNode.Parse(text) ?? throw new AiProviderException($"{s.Provider} returned an empty response.");
    }

    private static (string Url, bool Azure) Url(AiProviderSettings s, string operation, string model)
    {
        if (s.Provider == AiProviderNames.AzureOpenAI)
        {
            var endpoint = (s.Endpoint ?? throw new AiProviderException("Azure OpenAI needs an endpoint.")).TrimEnd('/');
            // Accept either the resource URL or a full deployment URL.
            var deploymentBase = endpoint.Contains("/openai/deployments/", StringComparison.OrdinalIgnoreCase)
                ? endpoint[..(endpoint.IndexOf("/openai/deployments/", StringComparison.OrdinalIgnoreCase))]
                : endpoint;
            return ($"{deploymentBase}/openai/deployments/{Uri.EscapeDataString(model)}/{operation}?api-version={AzureApiVersion}", true);
        }
        var baseUrl = (string.IsNullOrWhiteSpace(s.Endpoint) ? DefaultOpenAiEndpoint : s.Endpoint).TrimEnd('/');
        return ($"{baseUrl}/{operation}", false);
    }

    internal static string ErrorMessage(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            var message = node?["error"]?["message"]?.GetValue<string>() ?? node?["error"]?.ToString() ?? node?["message"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(message)) return message.Length > 300 ? message[..300] : message;
        }
        catch (JsonException) { }
        return body.Length > 300 ? body[..300] : body;
    }
}

/// <summary>OpenAI-compatible chat and embedding model.</summary>
public sealed class OpenAiCompatibleModel(OpenAiCompatibleClient client, AiProviderSettings settings) : IChatModel, IEmbeddingModel
{
    public string Provider => settings.Provider;
    public string Model => settings.Model;
    public bool IsLocal => false;

    public async Task<AiChatResponse> CompleteAsync(AiChatRequest request, CancellationToken ct)
    {
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = request.System } };
        foreach (var m in request.Messages) messages.Add(new JsonObject { ["role"] = m.Role, ["content"] = m.Content });
        var body = new JsonObject { ["model"] = settings.Model, ["messages"] = messages };
        var reasoningModel = settings.Model.StartsWith("o1", StringComparison.OrdinalIgnoreCase) || settings.Model.StartsWith("o3", StringComparison.OrdinalIgnoreCase)
                             || settings.Model.StartsWith("o4", StringComparison.OrdinalIgnoreCase) || settings.Model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase);
        if (settings.Provider == AiProviderNames.OpenAI || reasoningModel) body["max_completion_tokens"] = request.MaxTokens;
        else body["max_tokens"] = request.MaxTokens;
        if (!reasoningModel) body["temperature"] = request.Temperature;
        if (request.JsonMode) body["response_format"] = new JsonObject { ["type"] = "json_object" };
        var json = await client.PostAsync(settings, "chat/completions", settings.Model, body, ct);
        var choice = json["choices"]?[0];
        var content = choice?["message"]?["content"]?.GetValue<string>() ?? throw new AiProviderException($"{settings.Provider} returned no message content.");
        var usage = json["usage"];
        return new AiChatResponse(content, usage?["prompt_tokens"]?.GetValue<int>() ?? 0, usage?["completion_tokens"]?.GetValue<int>() ?? 0,
            json["model"]?.GetValue<string>() ?? settings.Model, choice?["finish_reason"]?.GetValue<string>());
    }

    public async Task<AiEmbeddingResponse> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        var input = new JsonArray();
        foreach (var t in texts) input.Add(string.IsNullOrEmpty(t) ? " " : t);
        var json = await client.PostAsync(settings, "embeddings", settings.Model, new JsonObject { ["model"] = settings.Model, ["input"] = input }, ct);
        var data = json["data"]?.AsArray() ?? throw new AiProviderException($"{settings.Provider} returned no embeddings.");
        var vectors = data.OrderBy(d => d?["index"]?.GetValue<int>() ?? 0)
            .Select(d => d!["embedding"]!.AsArray().Select(x => x!.GetValue<float>()).ToArray()).ToList();
        return new AiEmbeddingResponse(vectors, json["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0, settings.Model);
    }
}

/// <summary>Typed HttpClient for the Anthropic Messages API.</summary>
public sealed class AnthropicClient(HttpClient http)
{
    public const string DefaultEndpoint = "https://api.anthropic.com";
    public const string ApiVersion = "2023-06-01";

    public async Task<JsonNode> MessagesAsync(AiProviderSettings s, JsonObject body, CancellationToken ct)
    {
        var baseUrl = (string.IsNullOrWhiteSpace(s.Endpoint) ? DefaultEndpoint : s.Endpoint).TrimEnd('/');
        var url = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? $"{baseUrl}/messages" : $"{baseUrl}/v1/messages";
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Add("x-api-key", s.ApiKey ?? throw new AiProviderException("Anthropic needs an API key."));
        request.Headers.Add("anthropic-version", ApiVersion);
        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new AiProviderException($"Anthropic returned {(int)response.StatusCode} {response.ReasonPhrase}: {OpenAiCompatibleClient.ErrorMessage(text)}", (int)response.StatusCode);
        return JsonNode.Parse(text) ?? throw new AiProviderException("Anthropic returned an empty response.");
    }
}

/// <summary>Anthropic chat model (Messages API). JSON mode is achieved by instruction; the gateway extracts and validates the object.</summary>
public sealed class AnthropicModel(AnthropicClient client, AiProviderSettings settings) : IChatModel
{
    public string Provider => AiProviderNames.Anthropic;
    public string Model => settings.Model;
    public bool IsLocal => false;

    public async Task<AiChatResponse> CompleteAsync(AiChatRequest request, CancellationToken ct)
    {
        var messages = new JsonArray();
        foreach (var m in request.Messages) messages.Add(new JsonObject { ["role"] = m.Role == "assistant" ? "assistant" : "user", ["content"] = m.Content });
        var body = new JsonObject
        {
            ["model"] = settings.Model, ["max_tokens"] = request.MaxTokens, ["system"] = request.System, ["messages"] = messages,
            ["temperature"] = request.Temperature
        };
        var json = await client.MessagesAsync(settings, body, ct);
        var text = string.Concat((json["content"]?.AsArray() ?? []).Where(b => b?["type"]?.GetValue<string>() == "text").Select(b => b!["text"]!.GetValue<string>()));
        if (string.IsNullOrEmpty(text)) throw new AiProviderException("Anthropic returned no text content.");
        var usage = json["usage"];
        return new AiChatResponse(text, usage?["input_tokens"]?.GetValue<int>() ?? 0, usage?["output_tokens"]?.GetValue<int>() ?? 0,
            json["model"]?.GetValue<string>() ?? settings.Model, json["stop_reason"]?.GetValue<string>());
    }
}
