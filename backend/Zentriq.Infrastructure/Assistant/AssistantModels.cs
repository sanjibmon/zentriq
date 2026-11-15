using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Zentriq.Infrastructure.Assistant;

/// <summary>A read-only function the model may call to look at this installation (device status, health, scans).</summary>
public sealed record AssistantTool(string Name, string Description, JsonObject Parameters);

public sealed record ChatTurn(string Role, string Text);

public sealed record ModelReply(string Text, IReadOnlyList<string> ToolsUsed, int InputTokens, int OutputTokens);

public sealed class AssistantModelException(string message) : Exception(message);

/// <summary>An AI model behind the Zentriq Assistant: Claude (Anthropic API) or a local OpenAI-compatible server.</summary>
public interface IAssistantModel
{
    string Name { get; }
    Task<ModelReply> CompleteAsync(string system, IReadOnlyList<ChatTurn> turns, IReadOnlyList<AssistantTool> tools,
        Func<string, JsonElement, CancellationToken, Task<string>> runTool, CancellationToken ct);
}

internal static class AssistantHttp
{
    public static HttpClient Create(HttpMessageHandler? handler, TimeSpan timeout)
    {
        var http = handler is null
            ? new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, UseProxy = true, DefaultProxyCredentials = CredentialCache.DefaultCredentials })
            : new HttpClient(handler, disposeHandler: false);
        http.Timeout = timeout;
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Zentriq", "1.1"));
        return http;
    }

    public static async Task<JsonNode> PostAsync(HttpClient http, HttpRequestMessage req, string provider, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new AssistantModelException($"{provider} could not be reached: {ex.Message}"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new AssistantModelException($"{provider} did not answer in time."); }
        using (resp)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
            {
                string? msg = null;
                try { msg = JsonNode.Parse(body)?["error"]?["message"]?.GetValue<string>() ?? JsonNode.Parse(body)?["error"]?.GetValue<string>(); } catch (Exception) { }
                throw new AssistantModelException(resp.StatusCode switch
                {
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => $"{provider} refused the key or access ({(int)resp.StatusCode}). Check the key in System Settings › Assistant.",
                    HttpStatusCode.TooManyRequests => $"{provider} is rate limiting requests; try again in a minute.",
                    HttpStatusCode.NotFound => $"{provider}: the model or address was not found ({msg ?? "404"}).",
                    _ => $"{provider} answered {(int)resp.StatusCode}: {AssistantRedactor.Clean(msg ?? body[..Math.Min(body.Length, 300)])}",
                });
            }
            try { return JsonNode.Parse(body) ?? throw new AssistantModelException($"{provider} sent an empty answer."); }
            catch (JsonException) { throw new AssistantModelException($"{provider} sent an answer that is not JSON."); }
        }
    }
}

/// <summary>Claude through the Anthropic Messages API, with tool use (the customer's own API key).</summary>
public sealed class AnthropicModel(string apiKey, string model, string baseUrl = "https://api.anthropic.com", HttpMessageHandler? handler = null) : IAssistantModel
{
    public string Name => $"Claude ({model})";
    private const int MaxRounds = 6;

    public async Task<ModelReply> CompleteAsync(string system, IReadOnlyList<ChatTurn> turns, IReadOnlyList<AssistantTool> tools,
        Func<string, JsonElement, CancellationToken, Task<string>> runTool, CancellationToken ct)
    {
        using var http = AssistantHttp.Create(handler, TimeSpan.FromSeconds(90));
        var messages = new JsonArray();
        foreach (var t in turns) messages.Add(new JsonObject { ["role"] = t.Role == "assistant" ? "assistant" : "user", ["content"] = t.Text });
        var toolJson = new JsonArray();
        foreach (var t in tools) toolJson.Add(new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["input_schema"] = t.Parameters.DeepClone() });
        var used = new List<string>();
        int inTok = 0, outTok = 0;
        for (var round = 0; round < MaxRounds; round++)
        {
            var body = new JsonObject { ["model"] = model, ["max_tokens"] = 1600, ["system"] = system, ["messages"] = messages.DeepClone() };
            if (toolJson.Count > 0) body["tools"] = toolJson.DeepClone();
            using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/v1/messages")
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("x-api-key", apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            var resp = await AssistantHttp.PostAsync(http, req, "The Anthropic API", ct);
            inTok += resp["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
            outTok += resp["usage"]?["output_tokens"]?.GetValue<int>() ?? 0;
            var content = resp["content"]?.AsArray() ?? new JsonArray();
            var text = string.Join("\n", content.Where(c => c?["type"]?.GetValue<string>() == "text").Select(c => c!["text"]!.GetValue<string>()));
            var calls = content.Where(c => c?["type"]?.GetValue<string>() == "tool_use").ToList();
            if (resp["stop_reason"]?.GetValue<string>() != "tool_use" || calls.Count == 0) return new ModelReply(text.Trim(), used, inTok, outTok);
            messages.Add(new JsonObject { ["role"] = "assistant", ["content"] = content.DeepClone() });
            var results = new JsonArray();
            foreach (var c in calls)
            {
                var name = c!["name"]!.GetValue<string>();
                used.Add(name);
                var input = JsonSerializer.Deserialize<JsonElement>(c["input"]?.ToJsonString() ?? "{}");
                var output = await runTool(name, input, ct);
                results.Add(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = c["id"]!.GetValue<string>(), ["content"] = output });
            }
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = results });
        }
        throw new AssistantModelException("The model kept asking for more data; try a more specific question.");
    }
}

/// <summary>A local model through an OpenAI-compatible chat API (Ollama, LM Studio, vLLM, LocalAI) on the customer's network.</summary>
public sealed class OpenAiCompatibleModel(string baseUrl, string model, string? apiKey = null, HttpMessageHandler? handler = null) : IAssistantModel
{
    public string Name => $"Local model ({model})";
    private const int MaxRounds = 6;

    /// <summary>"http://host:11434" or ".../v1" both work.</summary>
    public static string ChatUrl(string baseUrl)
    {
        var u = baseUrl.TrimEnd('/');
        if (u.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return u;
        return (u.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? u : u + "/v1") + "/chat/completions";
    }

    public async Task<ModelReply> CompleteAsync(string system, IReadOnlyList<ChatTurn> turns, IReadOnlyList<AssistantTool> tools,
        Func<string, JsonElement, CancellationToken, Task<string>> runTool, CancellationToken ct)
    {
        using var http = AssistantHttp.Create(handler, TimeSpan.FromMinutes(3));
        var messages = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = system } };
        foreach (var t in turns) messages.Add(new JsonObject { ["role"] = t.Role == "assistant" ? "assistant" : "user", ["content"] = t.Text });
        var toolJson = new JsonArray();
        foreach (var t in tools)
            toolJson.Add(new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Parameters.DeepClone() } });
        var used = new List<string>();
        int inTok = 0, outTok = 0;
        for (var round = 0; round < MaxRounds; round++)
        {
            var body = new JsonObject { ["model"] = model, ["messages"] = messages.DeepClone(), ["temperature"] = 0.2, ["stream"] = false };
            if (toolJson.Count > 0) body["tools"] = toolJson.DeepClone();
            using var req = new HttpRequestMessage(HttpMethod.Post, ChatUrl(baseUrl)) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            if (!string.IsNullOrEmpty(apiKey)) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var resp = await AssistantHttp.PostAsync(http, req, "The local model server", ct);
            inTok += resp["usage"]?["prompt_tokens"]?.GetValue<int>() ?? 0;
            outTok += resp["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0;
            var msg = resp["choices"]?[0]?["message"] ?? throw new AssistantModelException("The local model server sent no answer.");
            var calls = msg["tool_calls"]?.AsArray();
            if (calls is null || calls.Count == 0) return new ModelReply((msg["content"]?.GetValue<string>() ?? "").Trim(), used, inTok, outTok);
            messages.Add(msg.DeepClone());
            foreach (var c in calls)
            {
                var fn = c?["function"];
                var name = fn?["name"]?.GetValue<string>() ?? "";
                used.Add(name);
                var argText = fn?["arguments"] is JsonValue v && v.TryGetValue<string>(out var str) ? str : fn?["arguments"]?.ToJsonString() ?? "{}";
                JsonElement args;
                try { args = JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(argText) ? "{}" : argText); }
                catch (JsonException) { args = JsonSerializer.Deserialize<JsonElement>("{}"); }
                var output = await runTool(name, args, ct);
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = c?["id"]?.GetValue<string>() ?? name, ["content"] = output });
            }
        }
        throw new AssistantModelException("The model kept asking for more data; try a more specific question.");
    }
}
