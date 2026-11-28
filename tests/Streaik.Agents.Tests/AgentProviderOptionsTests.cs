using System.Text.Json;
using Microsoft.Extensions.AI;
using Streaik.Agents.Json;

namespace Streaik.Agents.Tests;

public sealed class AgentProviderOptionsTests
{
    private const string Chat = "/api/chat";
    private const string Responses = "/v1/responses";

    [Theory]
    [InlineData(Chat, true)]
    [InlineData(Responses, true)]
    [InlineData(Responses, false)]
    public async Task Bundled_OpenAI_agent_sends_native_options_at_the_root_of_the_gateway_request(string endpoint, bool stream)
    {
        await using var host = await TestAgentHost.StartAsync();
        var definition = (await new JsonAgentSource().GetAgentsAsync()).Single(agent => agent.Name == "OpenAIAgent");
        var model = definition.Models![0]!;
        var body = endpoint == Chat
            ? (object)new { model = definition.Name, messages = new[] { new { id = "u1", role = "user", parts = new[] { new { type = "text", text = "Search for an answer." } } } } }
            : new { model = definition.Name, input = "Search for an answer.", stream };
        using var request = TestAgentHost.Post(endpoint, body, "test-key");
        using var response = await host.Client.SendAsync(request);
        var wire = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, wire);

        var call = Assert.Single(host.Gateway.Calls, sent => sent.Endpoint.AbsolutePath == "/base/v1/responses");
        Assert.Equal(model.Id, call.Body.GetProperty("model").GetString());
        Assert.Equal(stream, call.Body.GetProperty("stream").GetBoolean());
        Assert.Equal("responses_multi_agent=v1", call.Headers["OpenAI-Beta"]);
        var config = model.ProviderMetadata!;
        Assert.Contains(config["tools"].EnumerateArray(), tool => tool.GetProperty("type").ValueEquals("web_search"));
        foreach (var (name, value) in config)
        {
            var property = Assert.Single(call.Body.EnumerateObject(), property => property.NameEquals(name));
            Assert.True(JsonElement.DeepEquals(value, property.Value), $"Native option '{name}' changed on the wire.");
        }
        Assert.False(call.Body.TryGetProperty("providerMetadata", out _));
        Assert.False(call.Body.TryGetProperty("metadata", out _));
    }

    [Fact]
    public async Task Native_tools_coexist_with_runtime_tools_without_overriding_runtime_controls()
    {
        await using var host = await TestAgentHost.StartAsync();
        var body = Parse("""
            {"id":"c1","agent":{"name":"Mixed","instructions":"Use the configured tools.",
              "tools":[{"type":"skills","execution":"server","skills":[{"type":"skill_reference","skill_id":"anthropic/example"}]}],
              "model":{"id":"openai/test","providerMetadata":{
                "MODEL":"openai/other","input":"ignored","instructions":"ignored","store":true,"stream":false,
                "parallel_tool_calls":true,"tool_choice":"required","streaik_mcp_approvals":{},"streaik_mcp_history":{},
                "future_option":{"nested":[1,null,false]},
                "tools":[{"type":"tool_search","execution":"server","future":{"nested":[null,true]}},
                  {"type":"mcp","server_label":"unregistered"},{"type":"function","name":"browser_lookup"},
                  {"type":"skills","name":"skills","skills":[]}]}}},
              "tools":[{"name":"browser_lookup","inputSchema":{"type":"object","properties":{}}}],
              "messages":[{"id":"u1","role":"user","parts":[{"type":"text","text":"Use a tool."}]}]}
            """);
        using var request = TestAgentHost.Post(Chat, body, "test-key");
        using var response = await host.Client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

        var sent = Assert.Single(host.Gateway.Calls).Body;
        Assert.Equal("openai/test", sent.GetProperty("model").GetString());
        Assert.Equal("Use a tool.", sent.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString());
        Assert.Equal("Use the configured tools.", sent.GetProperty("instructions").GetString());
        Assert.False(sent.GetProperty("store").GetBoolean());
        Assert.True(sent.GetProperty("stream").GetBoolean());
        Assert.False(sent.GetProperty("parallel_tool_calls").GetBoolean());
        Assert.False(sent.TryGetProperty("tool_choice", out _));
        Assert.False(sent.TryGetProperty("streaik_mcp_approvals", out _));
        Assert.False(sent.TryGetProperty("streaik_mcp_history", out _));
        var config = body.GetProperty("agent").GetProperty("model").GetProperty("providerMetadata");
        Assert.True(JsonElement.DeepEquals(config.GetProperty("future_option"), sent.GetProperty("future_option")));
        Assert.Collection(sent.GetProperty("tools").EnumerateArray(),
            tool => Assert.Equal("browser_lookup", tool.GetProperty("name").GetString()),
            tool => Assert.Equal("skills", tool.GetProperty("name").GetString()),
            tool => Assert.True(JsonElement.DeepEquals(config.GetProperty("tools")[0], tool)));
    }

    [Fact]
    public async Task Tool_mode_none_suppresses_provider_tool_options_but_preserves_other_options()
    {
        var fixture = new GatewayFixture();
        using var gateway = new HttpClient(fixture) { BaseAddress = new Uri("https://gateway.example.test/") };
        var definition = Parse("""
            {"name":"ToolFree","model":{"id":"openai/test","providerMetadata":{
              "tools":[{"type":"web_search"}],"parallel_tool_calls":true,"multi_agent":{"enabled":true},"service_tier":"auto"}}}
            """).Deserialize<Agent>(JsonSerializerOptions.Web)!;

        await new AgentRuntime(gateway).RunAsync(definition, [new ChatMessage(ChatRole.User, "Answer without tools.")],
            options: new ChatOptions { ToolMode = ChatToolMode.None });

        var sent = Assert.Single(fixture.Calls).Body;
        Assert.Equal("none", sent.GetProperty("tool_choice").GetString());
        Assert.False(sent.TryGetProperty("tools", out _));
        Assert.False(sent.TryGetProperty("multi_agent", out _));
        Assert.False(sent.TryGetProperty("parallel_tool_calls", out _));
        Assert.Equal("auto", sent.GetProperty("service_tier").GetString());
    }

    private static JsonElement Parse(string json) => JsonSerializer.Deserialize<JsonElement>(json);
}
