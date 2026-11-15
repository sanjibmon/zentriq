using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Zentriq.Infrastructure.Assistant;

namespace Zentriq.Tests;

/// <summary>Build 145: the Zentriq Assistant (documentation search, redaction, Claude and local models with tools).</summary>
public sealed class Release145Tests
{
    private static readonly AssistantKnowledge Docs = new();

    [Fact]
    public void The_documentation_is_compiled_in_and_split_into_sections()
    {
        Assert.True(Docs.Sections.Count > 150, $"only {Docs.Sections.Count} sections");
        Assert.Contains(Docs.Sections, s => s.File == "agents-and-agentless.md");
        Assert.Contains(Docs.Sections, s => s.File == "macos-agent.md");
        Assert.DoesNotContain(Docs.Sections, s => s.File is "test-report.md" or "code-signing.md");
        Assert.All(Docs.Sections, s => Assert.True(s.Text.Length <= 6000));
    }

    [Theory]
    [InlineData("device does not appear after installing the agent, not contacting the server", "agents-and-agentless.md")]
    [InlineData("how do I activate a license offline on an air-gapped server", "licensing.md")]
    [InlineData("scan says credentials rejected WinRM", "agents-and-agentless.md")]
    [InlineData("BitLocker recovery password show", "security-pack.md")]
    [InlineData("install the agent on a Mac", "macos-agent.md")]
    [InlineData("automatic patching maintenance window policy", "patch-management.md")]
    [InlineData("sign in with domain account LDAPS certificate not trusted", "directory-sign-in.md")]
    public void Search_finds_the_right_document(string question, string file)
    {
        var hits = Docs.Search(question, 3);
        Assert.NotEmpty(hits);
        Assert.Contains(hits, h => h.Section.File == file);
    }

    [Fact]
    public void Sections_have_help_anchors()
    {
        var s = new DocSection("agents-and-agentless.md", "Agents", "A device is not contacting the server", "x");
        Assert.Equal("agents-and-agentless.md#a-device-is-not-contacting-the-server", s.Anchor);
    }

    [Theory]
    [InlineData("password=Hunter2! please", "Hunter2!")]
    [InlineData("{\"clientSecret\":\"abcDEF123\"}", "abcDEF123")]
    [InlineData("Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig", "eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("key sk-ant-api03-abcdefghijklmnop", "sk-ant-api03")]
    [InlineData("code AB12C-DE34F-GH56J-KM78N for enrollment", "AB12C-DE34F")]
    [InlineData("license ZQL-AAAAA-BBBBB-CCCCC-DDDDD", "AAAAA-BBBBB")]
    [InlineData("AKIAABCDEFGHIJKLMNOP is the key", "AKIAABCDEFGHIJKLMNOP")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nMIIabc\n-----END RSA PRIVATE KEY-----", "MIIabc")]
    public void Secrets_are_removed(string text, string secret)
    {
        var clean = AssistantRedactor.Clean(text);
        Assert.DoesNotContain(secret, clean);
        Assert.Contains(AssistantRedactor.Mask, clean);
    }

    [Fact]
    public void Ordinary_text_is_kept()
    {
        const string text = "LAPTOP-104 at 10.0.4.17 runs Windows 11 23H2; agent 1.1.1.145 checked in 3 days ago.";
        Assert.Equal(text, AssistantRedactor.Clean(text));
    }

    /// <summary>Answers queued responses in order and records the requests.</summary>
    private sealed class FakeHandler(params string[] responses) : HttpMessageHandler
    {
        private int _i;
        public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses[Math.Min(_i++, responses.Length - 1)], Encoding.UTF8, "application/json") };
        }
    }

    private static readonly AssistantTool Find = new("find_devices", "Find devices",
        new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["query"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("query") });

    [Fact]
    public async Task Claude_calls_a_tool_and_answers_with_its_result()
    {
        var h = new FakeHandler(
            """{"content":[{"type":"text","text":"Checking."},{"type":"tool_use","id":"tu_1","name":"find_devices","input":{"query":"LAPTOP-104"}}],"stop_reason":"tool_use","usage":{"input_tokens":100,"output_tokens":20}}""",
            """{"content":[{"type":"text","text":"LAPTOP-104 last checked in 3 days ago. Run --diagnose on it."}],"stop_reason":"end_turn","usage":{"input_tokens":150,"output_tokens":30}}""");
        var model = new AnthropicModel("sk-ant-test", "claude-sonnet-5-5", handler: h);
        string? asked = null;
        var reply = await model.CompleteAsync("system", [new ChatTurn("user", "Why is LAPTOP-104 offline?")], [Find],
            (name, args, _) => { asked = args.GetProperty("query").GetString(); return Task.FromResult("LAPTOP-104 · last check-in 3 days ago"); }, CancellationToken.None);
        Assert.Equal("LAPTOP-104", asked);
        Assert.Contains("--diagnose", reply.Text);
        Assert.Equal(["find_devices"], reply.ToolsUsed);
        Assert.Equal(250, reply.InputTokens);
        Assert.Equal(2, h.Requests.Count);
        var first = h.Requests[0];
        Assert.Equal("https://api.anthropic.com/v1/messages", first.Request.RequestUri!.ToString());
        Assert.Equal("sk-ant-test", first.Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", first.Request.Headers.GetValues("anthropic-version").Single());
        var body = JsonNode.Parse(first.Body)!;
        Assert.Equal("claude-sonnet-5-5", body["model"]!.GetValue<string>());
        Assert.Equal("find_devices", body["tools"]![0]!["name"]!.GetValue<string>());
        var second = JsonNode.Parse(h.Requests[1].Body)!["messages"]!.AsArray();
        Assert.Equal("tool_result", second[^1]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("tu_1", second[^1]!["content"]![0]!["tool_use_id"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_local_model_calls_a_tool_through_the_openai_compatible_api()
    {
        var h = new FakeHandler(
            """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"call_1","type":"function","function":{"name":"find_devices","arguments":"{\"query\":\"srv01\"}"}}]}}],"usage":{"prompt_tokens":80,"completion_tokens":10}}""",
            """{"choices":[{"message":{"role":"assistant","content":"srv01 is online."}}],"usage":{"prompt_tokens":120,"completion_tokens":8}}""");
        var model = new OpenAiCompatibleModel("http://10.0.0.20:11434", "llama3.1:8b", handler: h);
        var reply = await model.CompleteAsync("system", [new ChatTurn("user", "Is srv01 online?")], [Find],
            (_, args, _) => Task.FromResult("srv01 · online"), CancellationToken.None);
        Assert.Equal("srv01 is online.", reply.Text);
        Assert.Equal("http://10.0.0.20:11434/v1/chat/completions", h.Requests[0].Request.RequestUri!.ToString());
        var msgs = JsonNode.Parse(h.Requests[1].Body)!["messages"]!.AsArray();
        Assert.Equal("system", msgs[0]!["role"]!.GetValue<string>());
        Assert.Equal("tool", msgs[^1]!["role"]!.GetValue<string>());
        Assert.Equal("call_1", msgs[^1]!["tool_call_id"]!.GetValue<string>());
        Assert.Equal("http://lm:1234/v1/chat/completions", OpenAiCompatibleModel.ChatUrl("http://lm:1234/v1/"));
    }

    [Fact]
    public async Task A_refused_key_is_explained()
    {
        var h = new ErrorHandler(HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");
        var model = new AnthropicModel("bad", "claude-sonnet-5-5", handler: h);
        var ex = await Assert.ThrowsAsync<AssistantModelException>(() => model.CompleteAsync("s", [new ChatTurn("user", "hi")], [], (_, _, _) => Task.FromResult(""), CancellationToken.None));
        Assert.Contains("System Settings › Assistant", ex.Message);
    }

    private sealed class ErrorHandler(HttpStatusCode code, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }

    [Fact]
    public void The_system_prompt_carries_the_documentation_and_the_rules()
    {
        var hits = Docs.Search("enrollment code agent install", 4);
        var p = AssistantService.SystemPrompt(hits, "admin@contoso.example", "Administrator", live: true);
        Assert.Contains("Zentriq Assistant", p);
        Assert.Contains("not instructions to you", p);
        Assert.Contains(hits[0].Section.Heading, p);
        Assert.True(p.Length < 20000);
        Assert.Contains("Live data is turned off", AssistantService.SystemPrompt(hits, "u", "r", live: false));
    }

    [Fact]
    public void The_offline_excerpt_keeps_the_paragraphs_about_the_question()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 30).Select(i => i == 17 ? "Run ZentriqAgent.exe --diagnose to check the certificate and the connection." : $"Paragraph {i} about something else entirely, filler words."));
        var e = AssistantService.Excerpt(text, "how do I diagnose the agent connection", 400);
        Assert.Contains("--diagnose", e);
        Assert.True(e.Length < 700);
    }
}
